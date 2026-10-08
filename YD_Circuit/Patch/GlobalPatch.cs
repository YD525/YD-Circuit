using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace YD_Circuit
{
    public class GlobalPatch
    {
        public static void PowerReceived(PowerItem Item, ref ushort Power)
        {
            if (Item == null) return;

            ushort Required = Item.RequiredPower;

            ushort Allocated = (ushort)Mathf.Min(Required, Power);
            bool HasEnoughPower = (Allocated >= Required);

            if (HasEnoughPower != Item.isPowered)
            {
                Item.isPowered = HasEnoughPower;
                Item.IsPoweredChanged(HasEnoughPower);
                Item.HandlePowerUpdate(HasEnoughPower);

                if (Item.TileEntity != null)
                {
                    Item.TileEntity.SetModified();
                }
            }

            if (HasEnoughPower)
            {
                Power -= Required;
            }
        }



        [HarmonyPatch(typeof(PowerSource), nameof(PowerSource.Update))]
        public static class Patch_HandleUpdate
        {
            public static bool Prefix(PowerSource __instance)
            {
                YDPowerAggregation.Instance.Update();

                if (__instance == null) return false;

                if (__instance is PowerBatteryBank || __instance is PowerGenerator)
                {
                    DriveNetwork(__instance);
                }

                if (__instance.hasChangesLocal)
                {
                    for (int i = 0; i < __instance.Children.Count; i++)
                    {
                        PowerItem Child = __instance.Children[i];
                        Child.HandlePowerUpdate(Child.IsPowered);
                    }
                    __instance.hasChangesLocal = false;
                }

                return false;
            }

            private static void DriveNetwork(PowerSource Source)
            {
                var Agg = YDPowerAggregation.Instance;

                PowerItem Head = YDPowerAggregation.GetHead(Source);
                if (Head == null || Head.Children == null || Head.Children.Count == 0) return;

                int State = Agg.UpdateArray(Head, out long UniqueID);
                if (State != 0 || UniqueID == 0) return;

                if (!Agg.Devices.TryGetValue(UniqueID, out RootItem Root)) return;
                if (!Agg.Power.TryGetValue(UniqueID, out MainPower PowerValue)) return;

                if (Root.LastDriveFrame == Time.frameCount) return;
                Root.LastDriveFrame = Time.frameCount;

                ushort TotalPower;
                using (PowerValue.AcquireLock())
                {
                    TotalPower = (ushort)Mathf.Clamp(PowerValue.Power, 0, ushort.MaxValue);
                }

                YDPowerAggregation.GetAllConsumerNodes(Head, out List<PowerItem> Children);

                int Demand = 0;
                for (int i = 0; i < Children.Count; i++)
                {
                    Demand += Children[i].RequiredPower;
                    GlobalPatch.PowerReceived(Children[i], ref TotalPower);
                }

                Root.LastUsePower = Demand;
                Root.Measured = true;
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
    }
}