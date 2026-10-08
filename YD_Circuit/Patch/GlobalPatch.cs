using HarmonyLib;
public class GlobalPatch
{
    [HarmonyPatch(typeof(PowerSource), nameof(PowerSource.HandleSendPower))]
    public static class Patch_HandleSendPower
    {
        static bool Prefix(PowerSource __instance)
        {
            if (__instance == null)
                return false;

            if (__instance.Root != null)
                return false;

            if (!__instance.hasChangesLocal)
                return false;

            ushort TotalPower = 0;

            var Key = (PowerItem)__instance;

            if (YDPowerAggregation.Instance.Power.ContainsKey(Key))
            {
                var PowerValue = YDPowerAggregation.Instance.Power[Key];

                using (PowerValue.AcquireLock())
                {
                    TotalPower = (ushort)PowerValue.Power;
                }
            }

            ushort Before = TotalPower;
            var Children = __instance.Children;
            for (int i = 0; i < Children.Count; i++)
            {
                var Child = Children[i];
                if (Child is PowerSource) continue;
                Child.HandlePowerReceived(ref TotalPower);
                if (TotalPower <= 0) break;
            }

            __instance.LastPowerUsed = (ushort)(Before - TotalPower);

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

    [HarmonyPatch(typeof(PowerSource), nameof(PowerSource.Update))]
    public static class Patch_HandleUpdate
    {
        public static bool Prefix(PowerSource __instance)
        {
            YDPowerAggregation.Instance.Update(null);

            if (__instance != null)
            {
                if (__instance is PowerBatteryBank || __instance is PowerGenerator)
                {
                    if (__instance.Root == null)
                    {
                        YDPowerAggregation.Instance.Update(__instance);
                    }
                }
            }

            return true; 
        }
    }
}