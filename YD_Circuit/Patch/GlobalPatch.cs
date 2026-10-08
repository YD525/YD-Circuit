using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace YD_Circuit
{
    public class GlobalPatch
    {
        public static void PowerReceived(PowerItem Item, ref ushort power)
        {
            ushort num = (ushort)Mathf.Min(Item.RequiredPower, power);
            bool flag = num == Item.RequiredPower;
            if (flag != Item.isPowered)
            {
                Item.isPowered = flag;
                Item.IsPoweredChanged(flag);
                if (Item.TileEntity != null)
                {
                    Item.TileEntity.SetModified();
                }
            }
            power -= num;
        }


        [HarmonyPatch(typeof(PowerSource), nameof(PowerSource.Update))]
        public static class Patch_HandleUpdate
        {
            public static bool Prefix(PowerSource __instance)
            {
                YDPowerAggregation.Instance.Update();

                if (__instance != null)
                {
                    if (__instance is PowerBatteryBank || __instance is PowerGenerator)
                    {
                        if (__instance.Root == null)
                        {
                            if (__instance.Children.Count > 0)
                            {
                                int State = YDPowerAggregation.Instance.UpdateArray(__instance);

                                if (State > 0)
                                {
                                    Debug.Log("Add");
                                }
                                else
                                if (State == 0)
                                {
                                    ushort TotalPower = 0;

                                    var Key = new RootItem(((PowerItem)__instance)).UniqueID;

                                    if (YDPowerAggregation.Instance.Power.ContainsKey(Key))
                                    {
                                        var PowerValue = YDPowerAggregation.Instance.Power[Key];

                                        using (PowerValue.AcquireLock())
                                        {
                                            TotalPower = (ushort)PowerValue.Power;
                                        }
                                    }
                                    else
                                    {
                                        Debug.Log("NotSend");
                                    }

                                    ushort Before = TotalPower;

                                    YDPowerAggregation.GetAllConsumerNodes((PowerItem)__instance, out List<PowerItem> Children);

                                    for (int i = 0; i < Children.Count; i++)
                                    {
                                        var Child = Children[i];
                                        GlobalPatch.PowerReceived(Child, ref TotalPower);
                                        if (TotalPower <= 0) break;
                                    }

                                    Debug.Log("Count:" + Children.Count);

                                    int LastPowerUsed = (int)((int)Before - (int)TotalPower);
                                    Debug.Log("SendPower:" + LastPowerUsed);
                                }
                            }
                        }
                    }
                }

                return true;
            }
        }

        [HarmonyPatch(typeof(PowerSource), nameof(PowerSource.HandleSendPower))]
        public static class Patch_HandleSendPower
        {
            static bool Prefix(PowerSource __instance)
            {
                return false;
            }
        }

        [HarmonyPatch(typeof(PowerSource), nameof(PowerSource.CanParent))]
        public static class Patch_HandleCanParent
        {
            static bool Prefix(PowerSource __instance, ref bool __result)
            {
                if (__instance is PowerBatteryBank || __instance is PowerGenerator)
                {
                    __result = true;
                    return false;
                }

                return true;
            }
        }

        [HarmonyPatch(typeof(TileEntityPowerSource), nameof(TileEntityPowerSource.CanHaveParent))]
        public static class Patch_HandleCanHaveParent
        {
            static bool Prefix(TileEntityPowerSource __instance, IPowered powered, ref bool __result)
            {
                __result = true;
                return false;
            }
        }

        //[HarmonyPatch(typeof(ItemActionConnectPower), nameof(ItemActionConnectPower.OnHoldingUpdate))]
        //public static class Patch_HandleOnHoldingUpdate
        //{
        //    static void Postfix(ItemActionConnectPower __instance, ItemActionData _actionData)
        //    {
        //        TileEntityPowered tileEntityPowered = null;
        //        ConnectPowerData connectPowerData = (ConnectPowerData)_actionData;
        //        WorldRayHitInfo hitInfo = ((EntityPlayerLocal)_actionData.invData.holdingEntity).HitInfo;
        //        Vector3i blockPos = hitInfo.hit.blockPos;
        //        bool flag = true;
        //        if (connectPowerData.invData.holdingEntity is EntityPlayerLocal && connectPowerData.playerUI == null)
        //        {
        //            connectPowerData.playerUI = LocalPlayerUI.GetUIForPlayer(connectPowerData.invData.holdingEntity as EntityPlayerLocal);
        //        }

        //        if (connectPowerData.playerUI != null && !connectPowerData.invData.world.CanPlaceBlockAt(blockPos, connectPowerData.invData.world.gameManager.GetPersistentLocalPlayer()))
        //        {
        //            connectPowerData.isFriendly = false;
        //            connectPowerData.playerUI.nguiWindowManager.SetLabelText(EnumNGUIWindow.PowerInfo, null);

        //            Debug.Log("AA -1");
        //            return;
        //        }

        //        connectPowerData.isFriendly = true;
        //        if (hitInfo.bHitValid)
        //        {
        //            int num = (int)(Constants.cDigAndBuildDistance * Constants.cDigAndBuildDistance);
        //            if (hitInfo.hit.distanceSq <= (float)num)
        //            {
        //                BlockValue block = _actionData.invData.world.GetBlock(blockPos);
        //                if (block.Block is BlockPowered blockPowered)
        //                {
        //                    if (connectPowerData.playerUI != null)
        //                    {
        //                        Color value = Color.grey;
        //                        int num2 = blockPowered.RequiredPower;
        //                        if (blockPowered.isMultiBlock && block.ischild)
        //                        {
        //                            connectPowerData.playerUI.nguiWindowManager.SetLabelText(EnumNGUIWindow.PowerInfo, null);

        //                            Debug.Log("AA -2");
        //                            return;
        //                        }

        //                        Vector3i p = blockPos;
        //                        ChunkCluster chunkCache = _actionData.invData.world.ChunkCache;
        //                        if (chunkCache != null)
        //                        {
        //                            Chunk chunk = (Chunk)chunkCache.GetChunkSync(World.toChunkXZ(p.x), p.y, World.toChunkXZ(p.z));
        //                            if (chunk != null)
        //                            {
        //                                if (chunk.GetTileEntity(World.toBlock(p)) is TileEntityPowered tileEntityPowered2)
        //                                {
        //                                    value = (tileEntityPowered2.IsPowered ? Color.yellow : Color.grey);
        //                                    num2 = tileEntityPowered2.PowerUsed;
        //                                }
        //                                else
        //                                {
        //                                    value = Color.grey;
        //                                }
        //                            }
        //                        }

        //                        connectPowerData.playerUI.nguiWindowManager.SetLabel(EnumNGUIWindow.PowerInfo, $"{num2}W", value);
        //                    }

        //                    flag = false;
        //                }
        //            }
        //        }

        //        if (flag && connectPowerData.playerUI != null)
        //        {
        //            connectPowerData.playerUI.nguiWindowManager.SetLabelText(EnumNGUIWindow.PowerInfo, null);
        //        }

        //        if (connectPowerData.HasStartPoint)
        //        {
        //            if (connectPowerData.wireNode == null)
        //            {
        //                Debug.Log("AA -3");
        //                return;
        //            }

        //            float num3 = Vector3.Distance(connectPowerData.startPoint.ToVector3(), _actionData.invData.holdingEntity.position);
        //            if (num3 < (float)(__instance.maxWireLength - 5))
        //            {
        //                connectPowerData.inRange = true;
        //                connectPowerData.wireNode.wireColor = new Color(0f, 0f, 0f, 0f);
        //            }

        //            if (num3 > (float)(__instance.maxWireLength - 5))
        //            {
        //                connectPowerData.inRange = false;
        //                connectPowerData.wireNode.wireColor = Color.red;
        //            }

        //            if (num3 > (float)__instance.maxWireLength)
        //            {
        //                connectPowerData.HasStartPoint = false;
        //                if (connectPowerData.wireNode != null)
        //                {
        //                    WireManager.Instance.RemoveActiveWire(connectPowerData.wireNode);
        //                    UnityEngine.Object.Destroy(connectPowerData.wireNode.gameObject);
        //                    connectPowerData.wireNode = null;
        //                }

        //                if (!(connectPowerData.invData.world.GetChunkFromWorldPos(connectPowerData.startPoint) is Chunk))
        //                {
        //                    Debug.Log("AA -5");
        //                    return;
        //                }

        //                if (connectPowerData.invData.world.GetTileEntity(connectPowerData.startPoint) is TileEntityPowered)
        //                {
        //                    if (SingletonMonoBehaviour<ConnectionManager>.Instance.IsServer)
        //                    {
        //                        SingletonMonoBehaviour<ConnectionManager>.Instance.SendPackage(NetPackageManager.GetPackage<NetPackageWireToolActions>().Setup(NetPackageWireToolActions.WireActions.RemoveWire, Vector3i.zero, _actionData.invData.holdingEntity.entityId));
        //                    }
        //                    else
        //                    {
        //                        SingletonMonoBehaviour<ConnectionManager>.Instance.SendToServer(NetPackageManager.GetPackage<NetPackageWireToolActions>().Setup(NetPackageWireToolActions.WireActions.RemoveWire, Vector3i.zero, _actionData.invData.holdingEntity.entityId));
        //                    }
        //                }

        //                _actionData.invData.holdingEntity.RightArmAnimationUse = true;
        //                connectPowerData.invData.holdingEntity.PlayOneShot("ui_denied");
        //            }
        //        }

        //        if (!connectPowerData.StartLink || Time.time - connectPowerData.lastUseTime < AnimationDelayData.AnimationDelay[connectPowerData.invData.item.HoldType.Value].RayCast)
        //        {
        //            Debug.Log("AA -6" + connectPowerData.StartLink.ToString());
        //            return;
        //        }

        //        connectPowerData.StartLink = false;
        //        ConnectPowerData connectPowerData2 = (ConnectPowerData)_actionData;
        //        ItemInventoryData invData = _actionData.invData;
        //        _ = hitInfo.lastBlockPos;
        //        if (!hitInfo.bHitValid || hitInfo.tag.StartsWith("E_"))
        //        {
        //            connectPowerData2.HasStartPoint = false;
        //            Debug.Log("AA -7");
        //            return;
        //        }

        //        if (connectPowerData.invData.itemValue.MaxUseTimes > 0 && connectPowerData.invData.itemValue.UseTimes >= (float)connectPowerData.invData.itemValue.MaxUseTimes)
        //        {
        //            EntityPlayerLocal localPlayer = _actionData.invData.holdingEntity as EntityPlayerLocal;
        //            __instance.HandleJamSound(connectPowerData.invData.itemValue, __instance.item, localPlayer);
        //            Debug.Log("AA -8");
        //            return;
        //        }

        //        if (connectPowerData.invData.itemValue.MaxUseTimes > 0)
        //        {
        //            _actionData.invData.itemValue.UseTimes += EffectManager.GetValue(PassiveEffects.DegradationPerUse, _actionData.invData.itemValue, 1f, invData.holdingEntity, null, (_actionData.invData.itemValue.ItemClass != null) ? _actionData.invData.itemValue.ItemClass.ItemTags : FastTags<TagGroup.Global>.none) * ItemAction.ItemDegradationModifier;
        //            __instance.HandleItemBreak(_actionData);
        //        }
        //        if (connectPowerData2.HasStartPoint)
        //        {
        //            if (connectPowerData2.startPoint == hitInfo.hit.blockPos || !connectPowerData2.inRange || Vector3.Distance(connectPowerData.startPoint.ToVector3(), hitInfo.hit.blockPos.ToVector3()) > (float)__instance.maxWireLength)
        //            {
        //                Debug.Log("AA -9");
        //                return;
        //            }
        //            TileEntityPowered poweredBlock = __instance.GetPoweredBlock(invData);
        //            if (poweredBlock == null)
        //            {
        //                Debug.Log("AA -10");
        //                return;
        //            }
        //            TileEntityPowered poweredBlock2 = __instance.GetPoweredBlock(connectPowerData2.startPoint);
        //            if (poweredBlock2 == null)
        //            {
        //                Debug.Log("AA -11");
        //                return;
        //            }

        //            if (!poweredBlock.CanHaveParent(poweredBlock2))
        //            {
        //                GameManager.ShowTooltip(_actionData.invData.holdingEntity as EntityPlayerLocal, Localization.Get("ttCantHaveParent"));
        //                invData.holdingEntity.PlayOneShot("ui_denied");
        //                Debug.Log("AA -12");
        //                return;
        //            }

        //            if (poweredBlock2.ChildCount > 8)
        //            {
        //                GameManager.ShowTooltip(_actionData.invData.holdingEntity as EntityPlayerLocal, Localization.Get("ttWireLimit"));
        //                invData.holdingEntity.PlayOneShot("ui_denied");
        //                Debug.Log("AA -13");
        //                return;
        //            }
        //            //AAAAAAA
        //            poweredBlock.SetParentWithWireTool(poweredBlock2, invData.holdingEntity.entityId);
        //            _actionData.invData.holdingEntity.RightArmAnimationUse = true;
        //            connectPowerData2.HasStartPoint = false;
        //            if (SingletonMonoBehaviour<ConnectionManager>.Instance.IsServer)
        //            {
        //                SingletonMonoBehaviour<ConnectionManager>.Instance.SendPackage(NetPackageManager.GetPackage<NetPackageWireToolActions>().Setup(NetPackageWireToolActions.WireActions.RemoveWire, Vector3i.zero, _actionData.invData.holdingEntity.entityId));
        //            }
        //            else
        //            {
        //                SingletonMonoBehaviour<ConnectionManager>.Instance.SendToServer(NetPackageManager.GetPackage<NetPackageWireToolActions>().Setup(NetPackageWireToolActions.WireActions.RemoveWire, Vector3i.zero, _actionData.invData.holdingEntity.entityId));
        //            }

        //            EntityAlive holdingEntity = _actionData.invData.holdingEntity;
        //            string name = "wire_tool_" + (poweredBlock2.IsPowered ? "sparks" : "dust");
        //            Transform handTransform = __instance.GetHandTransform(holdingEntity);
        //            GameManager.Instance.SpawnParticleEffectServer(new ParticleEffect(name, handTransform.position + Origin.position, handTransform.rotation, holdingEntity.GetLightBrightness(), Color.white), invData.holdingEntity.entityId);
        //            if (connectPowerData.wireNode != null)
        //            {
        //                WireManager.Instance.RemoveActiveWire(connectPowerData.wireNode);
        //                UnityEngine.Object.Destroy(connectPowerData.wireNode.gameObject);
        //                connectPowerData.wireNode = null;
        //            }

        //            __instance.DecreaseDurability(connectPowerData);
        //            Debug.Log("AA -15");
        //            return;
        //        }

        //        TileEntityPowered poweredBlock3 = __instance.GetPoweredBlock(invData);
        //        if (poweredBlock3 == null)
        //        {
        //            Debug.Log("AA -16");
        //            return;
        //        }

        //        _actionData.invData.holdingEntity.RightArmAnimationUse = true;
        //        connectPowerData2.startPoint = hitInfo.hit.blockPos;
        //        connectPowerData2.HasStartPoint = true;
        //        EntityAlive holdingEntity2 = _actionData.invData.holdingEntity;
        //        if (SingletonMonoBehaviour<ConnectionManager>.Instance.IsServer)
        //        {
        //            SingletonMonoBehaviour<ConnectionManager>.Instance.SendPackage(NetPackageManager.GetPackage<NetPackageWireToolActions>().Setup(NetPackageWireToolActions.WireActions.AddWire, connectPowerData2.startPoint, holdingEntity2.entityId));
        //        }
        //        else
        //        {
        //            SingletonMonoBehaviour<ConnectionManager>.Instance.SendToServer(NetPackageManager.GetPackage<NetPackageWireToolActions>().Setup(NetPackageWireToolActions.WireActions.AddWire, connectPowerData2.startPoint, holdingEntity2.entityId));
        //        }

        //        Manager.BroadcastPlay(poweredBlock3.ToWorldPos().ToVector3(), poweredBlock3.IsPowered ? "wire_live_connect" : "wire_dead_connect");
        //        Transform handTransform2 = __instance.GetHandTransform(holdingEntity2);
        //        if (!(handTransform2 != null))
        //        {
        //            Debug.Log("AA -17");
        //            return;
        //        }

        //        Transform transform = handTransform2.FindInChilds("wire_mesh");
        //        if (!(transform == null))
        //        {
        //            if (connectPowerData2.wireNode != null)
        //            {
        //                WireManager.Instance.RemoveActiveWire(connectPowerData2.wireNode);
        //                UnityEngine.Object.Destroy(connectPowerData2.wireNode.gameObject);
        //                connectPowerData2.wireNode = null;
        //            }

        //            WireNode component = ((GameObject)UnityEngine.Object.Instantiate(Resources.Load("Prefabs/WireNode"))).GetComponent<WireNode>();
        //            component.LocalPosition = hitInfo.hit.blockPos.ToVector3() - Origin.position;
        //            component.localOffset = poweredBlock3.GetWireOffset();
        //            component.localOffset.x += 0.5f;
        //            component.localOffset.y += 0.5f;
        //            component.localOffset.z += 0.5f;
        //            component.Source = transform.gameObject;
        //            component.sourceOffset = __instance.wireOffset;
        //            component.TogglePulse(isOn: false);
        //            component.SetPulseSpeed(360f);
        //            connectPowerData2.wireNode = component;
        //            WireManager.Instance.AddActiveWire(component);
        //            string name2 = "wire_tool_" + (poweredBlock3.IsPowered ? "sparks" : "dust");
        //            GameManager.Instance.SpawnParticleEffectServer(new ParticleEffect(name2, handTransform2.position + Origin.position, handTransform2.rotation, holdingEntity2.GetLightBrightness(), Color.white), invData.holdingEntity.entityId);
        //        }
        //    }
        //}


    }
}