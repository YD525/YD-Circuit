
using System;
using UnityEngine;

namespace YD_Circuit
{
    public class Log
    {
        public static void Out(string Msg)
        {
            Debug.Log("Out->" + Msg);
        }
        public static void Exception(string Msg)
        {
            Debug.Log("Error->" + Msg);
        }
        public static void Exception(Exception ex)
        {
            Exception(ex.Message);
        }
    }
}