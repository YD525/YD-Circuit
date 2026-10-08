using HarmonyLib;
using System.Reflection;
using UnityEngine;

namespace YD_Circuit
{
    public class Circuit : IModApi
    {
        public static string Version = "1.0.1";
        public void InitMod(Mod _modInstance)
        {
            Debug.Log("YD Circuit Ver: " + Version);

            new Harmony("YD.Circuit").PatchAll(Assembly.GetExecutingAssembly());

            YDPowerAggregation.Instance.Start();
        }
    }
}
