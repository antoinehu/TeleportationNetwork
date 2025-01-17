﻿using ProtoBuf;
using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace TeleportationNetwork
{
    public class TeleportNetworkServer : TeleportNetwork, ITeleportNetworkServer
    {
        ICoreServerAPI ServerApi => Api as ICoreServerAPI;
        IServerNetworkChannel ServerChannel => Channel as IServerNetworkChannel;

        public override void Init(ICoreAPI api, ITeleportManager manager)
        {
            base.Init(api, manager);

            ServerApi.Event.PlayerJoin += PushTeleports;
            ServerApi.Event.SaveGameLoaded += OnLoadGame;
            ServerApi.Event.GameWorldSave += OnSaveGame;

            ServerChannel
                .RegisterMessageType(typeof(TeleportingData))
                .RegisterMessageType(typeof(SyncTeleportMessage))
                .SetMessageHandler<TeleportingData>(OnTeleport)
                .SetMessageHandler<SyncTeleportMessage>(OnReceiveSyncPacket);
        }


        private void PushTeleports(IServerPlayer byPlayer)
        {
            foreach (var teleport in Manager.GetAllTeleports())
            {
                ServerChannel.SendPacket(new SyncTeleportMessage
                {
                    Teleport = teleport as Teleport,
                    DoRemove = false
                }, byPlayer);
            }
        }

        private void OnSaveGame()
        {
            var list = Manager.GetAllTeleports().Select(t => t as Teleport).ToList();
            ServerApi.WorldManager.SaveGame.StoreData("TPNetData", SerializerUtil.Serialize(list));
        }

        private void OnLoadGame()
        {
            try
            {
                Core.ModLogger.Event("Start loading data");
                byte[] data = ServerApi.WorldManager.SaveGame.GetData("TPNetData");

                var list = new List<ITeleport>();

                if (data != null)
                {
                    try
                    {
                        list = SerializerUtil
                            .Deserialize<List<Teleport>>(data)
                            .Select(t => t as ITeleport)
                            .ToList();
                    }
                    catch (ProtoException e)
                    {
                        try
                        {
                            Core.ModLogger.Debug("Old world? Trying legacy loader");
                            var legacyData = SerializerUtil.Deserialize<Dictionary<BlockPos, LegacyTeleportData>>(data);
                            foreach (var el in legacyData)
                            {
                                var tp = (Teleport)Manager.GetOrCreateTeleport(el.Key, el.Value.Available);
                                tp.Name = el.Value.Name;
                                tp.ActivatedByPlayers = el.Value.ActivatedBy;
                                Manager.SetTeleport(tp);
                            }
                        }
                        catch (Exception e2)
                        {
                            Core.ModLogger.Debug("Failed loading data:\n{0}, failed legacy loading:\n{1}", e, e2);
                            throw e;
                        }
                    }
                }

                foreach (var teleport in list)
                {
                    Manager.SetTeleport(teleport);
                    Core.ModLogger.Debug($"Loaded teleport data for {teleport.Name} at {teleport.Pos}");
                }

                Core.ModLogger.Event("Data loaded");
            }
            catch (Exception e)
            {
                Core.ModLogger.Error("Failed loading data:\n{0}", e);
            }
        }

        private void OnTeleport(IServerPlayer fromPlayer, TeleportingData data)
        {
            Entity[] tpEntities = null;
            Vec3d currCenterPos = null;

            if (data.SourcePos == null)
            {
                if (fromPlayer.Entity != null)
                {
                    tpEntities = new Entity[] { fromPlayer.Entity };
                    currCenterPos = fromPlayer.Entity.Pos.XYZ;
                }
            }
            else
            {
                if (ServerApi.World.BlockAccessor.GetBlockEntity(data.SourcePos.AsBlockPos) is BETeleport be)
                {
                    currCenterPos = be.Pos.ToVec3d().Add(0.5, 1, 0.5);
                    tpEntities = MathUtil.GetInCircleEntities(Api, Constants.SealRadius, currCenterPos);
                }
            }

            if (tpEntities == null || data.TargetPos == null) return;


            var systemTemporalStability = ServerApi.ModLoader.GetModSystem<SystemTemporalStability>();
            bool stabilityEnabled = ServerApi.World.Config.GetBool("temporalStability", true);

            string name = Manager.GetTeleport(data.TargetPos.AsBlockPos)?.Name;

            foreach (var entity in tpEntities)
            {
                double x = data.TargetPos.X + (entity.Pos.X - currCenterPos.X) + 0.5;
                double y = data.TargetPos.Y + (entity.Pos.Y - currCenterPos.Y) + 2;
                double z = data.TargetPos.Z + (entity.Pos.Z - currCenterPos.Z) + 0.5;

                if (entity is EntityPlayer entityPlayer)
                {
                    entityPlayer.SetActivityRunning(Core.ModId + "_teleportCooldown", Config.Current.TeleportCooldown);

                    bool unstableTeleport = Config.Current.StabilityTeleportMode.Val == "always";

                    if (stabilityEnabled)
                    {
                        double currStability = entityPlayer.WatchedAttributes.GetDouble("temporalStability");
                        double newStability = currStability - Config.Current.StabilityConsumable.Val;

                        if (newStability < 0 || systemTemporalStability.StormData.nowStormActive)
                        {
                            entityPlayer.WatchedAttributes.SetDouble("temporalStability", Math.Max(0, newStability));
                            unstableTeleport = true;
                        }
                        else if (0 < newStability && newStability < currStability)
                        {
                            entityPlayer.WatchedAttributes.SetDouble("temporalStability", newStability);
                        }
                    }

                    if (Config.Current.StabilityTeleportMode.Val != "off" && unstableTeleport)
                    {
                        Commands.RandomTeleport(fromPlayer, Config.Current.UnstableTeleportRange.Val, new Vec3i((int)x, (int)y, (int)z));
                    }
                    else
                    {
                        entity.TeleportToDouble(x, y, z);
                    }
                }
                else
                {
                    entity.TeleportToDouble(x, y, z);
                }

                Core.ModLogger.Notification($"{entity?.GetName()} teleported to {x}, {y}, {z} ({name})");
            }
        }

        private void OnReceiveSyncPacket(IServerPlayer fromPlayer, SyncTeleportMessage message)
        {
            if (message.Teleport != null)
            {
                if (message.DoRemove)
                {
                    Manager.RemoveTeleport(message.Teleport);
                }
                else
                {
                    Manager.SetTeleport(message.Teleport);
                }

                ServerChannel.BroadcastPacket(message, fromPlayer);
            }
        }

        protected override void OnTeleportAdded(ITeleport teleport)
        {
            ServerChannel.BroadcastPacket(new SyncTeleportMessage
            {
                Teleport = teleport as Teleport,
                DoRemove = false
            });

            base.OnTeleportAdded(teleport);
        }

        protected override void OnTeleportModified(ITeleport teleport)
        {
            ServerChannel.BroadcastPacket(new SyncTeleportMessage
            {
                Teleport = teleport as Teleport,
                DoRemove = false
            });

            base.OnTeleportModified(teleport);
        }

        protected override void OnTeleportRemoved(ITeleport teleport)
        {
            ServerChannel.BroadcastPacket(new SyncTeleportMessage
            {
                Teleport = teleport as Teleport,
                DoRemove = true
            });

            base.OnTeleportRemoved(teleport);
        }

        protected override void OnTeleportActivatedByPlayer(ITeleport teleport, IPlayer player)
        {
            ServerChannel.BroadcastPacket(new SyncTeleportMessage
            {
                Teleport = teleport as Teleport,
                DoRemove = false
            });

            base.OnTeleportActivatedByPlayer(teleport, player);
        }
    }

    [ProtoContract]
    public class LegacyTeleportData
    {
        [ProtoMember(1)] public string Name = "";
        [ProtoMember(2)] public bool Available = false;
        [ProtoMember(3)] public List<string> ActivatedBy = new();
    }
}