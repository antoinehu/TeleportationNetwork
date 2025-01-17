using System;
using System.Collections.Generic;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace TeleportationNetwork
{
    public class BETeleport : BlockEntity
    {
        public static AssetLocation DefaultFrameCode => new AssetLocation("game:stonebricks-granite");

        public bool Active { get; set; }
        public bool Enabled => Block is BlockNormalTeleport;
        public ITeleportManager TeleportManager { get; private set; }

        TeleportRenderer renderer;
        TeleportParticleManager particleManager;

        float activeStage;
        Dictionary<string, TeleportingPlayer> activePlayers;

        GuiDialogTeleportList teleportDlg;
        GuiDialogRenameTeleport renameDlg;

        long? animListenerId;
        BlockEntityAnimationUtil AnimUtil => GetBehavior<BEBehaviorAnimatable>()?.animUtil;

        MeshData frameMesh;
        private ItemStack _frameStack;
        public ItemStack FrameStack
        {
            get => _frameStack;
            set
            {
                _frameStack = value;

                UpdateFrameMesh();
                MarkDirty(true);
            }
        }

        public override void Initialize(ICoreAPI api)
        {
            base.Initialize(api);

            activePlayers = new Dictionary<string, TeleportingPlayer>();
            TeleportManager = api.ModLoader.GetModSystem<TeleportSystem>().Manager;

            if (_frameStack == null)
            {
                _frameStack = new ItemStack(api.World.GetBlock(DefaultFrameCode));
            }

            if (api.Side == EnumAppSide.Server)
            {
                TeleportManager.GetOrCreateTeleport(Pos, Enabled);
            }
            else
            {
                particleManager = new TeleportParticleManager(api as ICoreClientAPI, Pos);
                renderer = new TeleportRenderer(Pos, api as ICoreClientAPI);
                UpdateFrameMesh();

                if (AnimUtil != null)
                {
                    float rotY = Block.Shape.rotateY;
                    AnimUtil.InitializeAnimator(Core.ModId + "-teleport", new Vec3f(0, rotY, 0));

                    if (AnimUtil.activeAnimationsByAnimCode.Count == 0 ||
                        AnimUtil.animator.ActiveAnimationCount == 0)
                    {
                        AnimUtil.StartAnimation(new AnimationMetaData()
                        {
                            Animation = "largegears",
                            Code = "largegears",
                            AnimationSpeed = 25f,
                            EaseInSpeed = float.MaxValue,
                            EaseOutSpeed = float.MaxValue
                        });
                        AnimUtil.StartAnimation(new AnimationMetaData()
                        {
                            Animation = "smallgears",
                            Code = "smallgears",
                            AnimationSpeed = 50f,
                            EaseInSpeed = float.MaxValue,
                            EaseOutSpeed = float.MaxValue
                        });
                    }
                }
            }

            if (Enabled)
            {
                animListenerId = RegisterGameTickListener(OnGameTick, 50);
            }
        }

        public void OnEntityCollide(Entity entity)
        {
            if (Enabled && entity is EntityPlayer player)
            {
                if (player.IsActivityRunning(Core.ModId + "_teleportCooldown"))
                {
                    return;
                }

                if (!activePlayers.TryGetValue(player.PlayerUID, out TeleportingPlayer tpe))
                {
                    activePlayers[player.PlayerUID] = tpe = new TeleportingPlayer()
                    {
                        Player = player,
                        State = EnumTeleportingEntityState.None
                    };
                }

                tpe.LastCollideMs = Api.World.ElapsedMilliseconds;
                Active = true;
            }
        }

        private void OnGameTick(float dt)
        {
            if (Api.Side == EnumAppSide.Client)
            {
                particleManager.SpawnSealEdgeParticle();
                renderer.Speed = (float)(1 + Math.Exp(activeStage) * 1f);
                renderer.Progress = activeStage;
            }

            if (Active)
            {
                CheckActivePlayers(dt);

                if (Api.Side == EnumAppSide.Client)
                {
                    particleManager.SpawnActiveParticles();
                }
            }
        }

        private void CheckActivePlayers(float dt)
        {
            var toRemove = new List<string>();

            float maxSecondsPassed = 0;
            foreach (var activePlayer in activePlayers)
            {
                if (activePlayer.Value.State == EnumTeleportingEntityState.None)
                {
                    var player = Api.World.PlayerByUid(activePlayer.Key);
                    ITeleport teleport = TeleportManager.GetTeleport(Pos);
                    TeleportManager.ActivateTeleport(teleport, player);
                    activePlayer.Value.State = EnumTeleportingEntityState.Teleporting;
                }

                activePlayer.Value.SecondsPassed += Math.Min(0.5f, dt);

                if (Api.World.ElapsedMilliseconds - activePlayer.Value.LastCollideMs > 300)
                {
                    toRemove.Add(activePlayer.Key);
                    continue;
                }

                if (activePlayer.Value.SecondsPassed > Constants.BeforeTeleportShowGUITime && activePlayer.Value.State == EnumTeleportingEntityState.Teleporting)
                {
                    activePlayer.Value.State = EnumTeleportingEntityState.UI;

                    if (Api.Side == EnumAppSide.Client && teleportDlg?.IsOpened() != true)
                    {
                        if (teleportDlg != null) teleportDlg.Dispose();

                        teleportDlg = new GuiDialogTeleportList(Api as ICoreClientAPI, Pos);
                        teleportDlg.TryOpen();
                    }
                }

                maxSecondsPassed = Math.Max(activePlayer.Value.SecondsPassed, maxSecondsPassed);
            }

            foreach (var playerUID in toRemove)
            {
                activePlayers.Remove(playerUID);

                if (Api.Side == EnumAppSide.Client)
                {
                    teleportDlg?.TryClose();
                }
            }

            Active = activePlayers.Count > 0;
            activeStage = Math.Min(1, maxSecondsPassed / Constants.BeforeTeleportShowGUITime);
        }

        public override void OnLoadCollectibleMappings(IWorldAccessor worldForNewMappings, Dictionary<int, AssetLocation> oldBlockIdMapping, Dictionary<int, AssetLocation> oldItemIdMapping, int schematicSeed)
        {
            if (_frameStack == null)
            {
                _frameStack = new ItemStack(worldForNewMappings.GetBlock(DefaultFrameCode));
            }
            else
            {
                _frameStack.FixMapping(oldBlockIdMapping, oldItemIdMapping, worldForNewMappings);
            }

            base.OnLoadCollectibleMappings(worldForNewMappings, oldBlockIdMapping, oldItemIdMapping, schematicSeed);
        }

        public void UpdateFrameMesh()
        {
            if (Api is ICoreClientAPI capi)
            {
                var shapeCode = new AssetLocation(Core.ModId, "shapes/block/teleport/frame.json");
                Shape frameShape = Api.Assets.Get<Shape>(shapeCode);
                capi.Tesselator.TesselateShape(_frameStack.Collectible, frameShape, out frameMesh);
            }
        }

        public void OpenRenameDlg()
        {
            if (Api.Side == EnumAppSide.Client)
            {
                if (renameDlg == null)
                {
                    renameDlg = new GuiDialogRenameTeleport(Pos, Api as ICoreClientAPI);
                }

                if (!renameDlg.IsOpened()) renameDlg.TryOpen();
            }
        }

        public override void OnReceivedClientPacket(IPlayer fromPlayer, int packetid, byte[] data)
        {
            base.OnReceivedClientPacket(fromPlayer, packetid, data);

            if (packetid == Constants.ChangeTeleportNamePacketId)
            {
                if (Api.World.Claims.TryAccess(fromPlayer, Pos, EnumBlockAccessFlags.Use))
                {
                    Teleport teleport = TeleportManager.GetTeleport(Pos) as Teleport
                            ?? new Teleport() { Pos = Pos, Enabled = Enabled };

                    teleport.Name = Encoding.UTF8.GetString(data);
                    TeleportManager.SetTeleport(teleport);
                }
            }
        }

        public override void GetBlockInfo(IPlayer forPlayer, StringBuilder dsc)
        {
            base.GetBlockInfo(forPlayer, dsc);

            if (Enabled)
            {
                ITeleport teleport = TeleportManager.GetTeleport(Pos);
                if (teleport != null)
                {
                    dsc.AppendLine(teleport.Name);

                    if (forPlayer.WorldData.CurrentGameMode == EnumGameMode.Creative)
                    {
                        dsc.AppendLine("Neighbours:");
                        foreach (BlockPos nodePos in teleport.Neighbours)
                        {
                            string name = "null";
                            if (nodePos != null)
                            {
                                ITeleport node = TeleportManager.GetTeleport(nodePos);
                                if (node != null)
                                {
                                    name = node.Name;
                                }
                            }
                            dsc.AppendLine("*** " + name);
                        }
                    }
                }
            }
        }

        public override void OnBlockUnloaded()
        {
            base.OnBlockUnloaded();
            renderer?.Dispose();
        }

        public override void OnBlockRemoved()
        {
            base.OnBlockRemoved();

            if (Api.Side == EnumAppSide.Server)
            {
                ITeleport teleport = TeleportManager.GetTeleport(Pos);
                if (teleport != null)
                {
                    TeleportManager.RemoveTeleport(teleport);
                }
            }

            renderer?.Dispose();
        }

        public override bool OnTesselation(ITerrainMeshPool mesher, ITesselatorAPI tessThreadTesselator)
        {
            mesher.AddMeshData(frameMesh);
            return base.OnTesselation(mesher, tessThreadTesselator);
        }

        public override void ToTreeAttributes(ITreeAttribute tree)
        {
            base.ToTreeAttributes(tree);
            tree.SetItemstack("frameStack", _frameStack);
        }

        public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldAccessForResolve)
        {
            base.FromTreeAttributes(tree, worldAccessForResolve);
            _frameStack = tree.GetItemstack("frameStack");
            _frameStack.ResolveBlockOrItem(worldAccessForResolve);
        }
    }
}