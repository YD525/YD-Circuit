using System.Collections.Concurrent;
using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using static PowerItem;
using System.Linq;

namespace YD_Circuit
{
    public class YDPowerAggregation
    {
        public static YDPowerAggregation Instance = new YDPowerAggregation();

        public ConcurrentDictionary<long, RootItem> Devices = new ConcurrentDictionary<long, RootItem>();
        public ConcurrentDictionary<long, MainPower> Power = new ConcurrentDictionary<long, MainPower>();

        public Thread PowerTrd = null;
        private ConcurrentQueue<Action> TrdJobs = new ConcurrentQueue<Action>();

        public void CheckExpired()
        {
            if (Devices == null) return;

            UnitySelfTrd(() =>
            {
                foreach (var Pair in Devices)
                {
                    var Item = Pair.Value;
                    bool Expired = Item == null || Item.Object == null || Item.Object.Root != null || Item.Object.Parent != null 
                        || !PowerManager.Instance.PowerItemDictionary.TryGetValue(Item.Object.Position, out var Cur)
                        || Cur != Item.Object;

                    if (Item.Object.Children != null)
                    {
                        if (Item.Object.Children.Count == 0)
                        {
                            Expired = true;
                        }
                    }

                    if (Expired)
                    {
                        Devices.TryRemove(Pair.Key, out _);
                        Power.TryRemove(Pair.Key, out _);
                    }
                }
            });
        }

        public static void GetAllConsumerNodes(PowerItem StartNode, out List<PowerItem> Consumers)
        {
            Consumers = new List<PowerItem>();

            if (StartNode == null)
                return;

            HashSet<PowerItem> Visited = new HashSet<PowerItem>();

            Visited.Add(StartNode);

            if (StartNode.PowerChildren() && StartNode.Children != null)
            {
                for (int i = 0; i < StartNode.Children.Count; i++)
                {
                    TraverseConsumerNodes(StartNode.Children[i], Visited, Consumers);
                }
            }
        }

        private static void TraverseConsumerNodes(PowerItem CurrentNode, HashSet<PowerItem> Visited, List<PowerItem> Consumers)
        {
            if (CurrentNode == null)
                return;

            if (!Visited.Add(CurrentNode))
                return;

            if (CurrentNode.PowerItemType != PowerItemTypes.Generator &&
                CurrentNode.PowerItemType != PowerItemTypes.BatteryBank)
            {
                Consumers.Add(CurrentNode);
            }

            if (CurrentNode.PowerChildren() && CurrentNode.Children != null)
            {
                for (int i = 0; i < CurrentNode.Children.Count; i++)
                {
                    TraverseConsumerNodes(CurrentNode.Children[i], Visited, Consumers);
                }
            }
        }
        public static void GetAllConnectedNodes(PowerItem StartNode, out List<PowerSourceInFo> InFos, out Dictionary<long, PowerItem> Pool)
        {
            InFos = new List<PowerSourceInFo>();
            Pool = new Dictionary<long, PowerItem>();

            if (StartNode == null)
                return;

            HashSet<PowerItem> Visited = new HashSet<PowerItem>();

            TraverseConnectedNodes(StartNode, Visited, InFos, Pool);
        }

        private static void TraverseConnectedNodes(PowerItem CurrentNode, HashSet<PowerItem> Visited, List<PowerSourceInFo> InFos, Dictionary<long, PowerItem> Pool)
        {
            if (CurrentNode == null)
                return;

            if (!Visited.Add(CurrentNode))
                return;

            long ItemUniqueID = IDGen.GetUniqueID64(CurrentNode);

            Pool[ItemUniqueID] = CurrentNode;

            ushort MaxOutput = 0;
            ushort CurrentPower = 0;
            ushort CurrentFuel = 0;
            PowerItemTypes PType = CurrentNode.PowerItemType;

            if (CurrentNode is PowerGenerator Generator)
            {
                MaxOutput = Generator.MaxOutput;
                CurrentPower = Generator.CurrentPower;
                CurrentFuel = Generator.CurrentFuel;
            }
            else
            if (CurrentNode is PowerSource Source)
            {
                MaxOutput = Source.MaxOutput;
                CurrentPower = Source.CurrentPower;
            }

            bool IsRoot = false;

            if (CurrentNode.Root == null)
            {
                IsRoot = true;
            }

            InFos.Add(new PowerSourceInFo
            {
                UniqueID = ItemUniqueID,
                Object = CurrentNode,
                MaxOutput = MaxOutput,
                CurrentPower = CurrentPower,
                CurrentFuel = CurrentFuel,
                Type = PType,
                IsRoot = IsRoot
            });

            if (CurrentNode.PowerChildren())
            {
                for (int i = 0; i < CurrentNode.Children.Count; i++)
                {
                    TraverseConnectedNodes(CurrentNode.Children[i], Visited, InFos, Pool);
                }
            }
        }

        private int UnityThreadId;
        public void Start()
        {
            UnityThreadId = System.Environment.CurrentManagedThreadId;

            if (PowerTrd == null)
            {
                PowerTrd = new Thread(() =>
                {
                    while (true)
                    {
                        // Run the power balance check once per second
                        Thread.Sleep(1000);
                        
                        try
                        {
                            CheckExpired();

                            var Array = Devices.ToList();

                            Debug.Log("Circuit Check..." + Array.Count);

                            for (int i = 0; i < Array.Count; i++)
                            {
                                long UniqueID = Array[i].Value.UniqueID;

                                int UsedPower = 0;

                                bool IsOK = UnitySelfTrd(() =>
                                {
                                    UsedPower = CalculateSubtreePowerRequired(Array[i].Value.Object);
                                });

                                if (!IsOK)
                                {
                                    continue;
                                }

                                List<long> Banks = new List<long>();
                                List<long> Generators = new List<long>();

                                List<PowerSourceInFo> Nodes = null;
                                Dictionary<long, PowerItem> Pool = null;

                                UnitySelfTrd(() =>
                                {
                                    GetAllConnectedNodes(Array[i].Value.Object, out List<PowerSourceInFo> SetInFos, out Dictionary<long, PowerItem> SetPool);
                                    Nodes = SetInFos;
                                    Pool = SetPool;
                                });

                                if (Nodes == null)
                                {
                                    continue;
                                }

                                if (!Power.ContainsKey(UniqueID))
                                {
                                    Power.TryAdd(UniqueID, new MainPower());
                                }

                                MainPower PowerValue = Power[UniqueID];

                                using (PowerValue.AcquireLock())
                                {
                                    PowerValue.BatteryTotalPower = 0;
                                    PowerValue.GeneratorTotalPower = 0;
                                    PowerValue.ExpectedGeneratorTotalPower = 0;
                                }

                                for (int ir = 0; ir < Nodes.Count; ir++)
                                {
                                    var InFo = Nodes[ir];

                                    if (InFo != null)
                                    {
                                        if (InFo.Type == PowerItemTypes.Generator || InFo.Type == PowerItemTypes.BatteryBank)
                                        {
                                            ushort MaxOutput = 0;
                                            ushort CurrentPower = 0;
                                            ushort CurrentFuel = 0;

                                            PowerItemTypes Type = PowerItemTypes.None;

                                            MaxOutput = InFo.MaxOutput;
                                            CurrentPower = InFo.CurrentPower;
                                            CurrentFuel = InFo.CurrentFuel;

                                            Type = InFo.Type;

                                            if (Type == PowerItem.PowerItemTypes.BatteryBank)
                                            {
                                                // Empty batteries cannot supply anything
                                                if (CurrentPower == 0)
                                                {
                                                    continue;
                                                }

                                                using (PowerValue.AcquireLock())
                                                {
                                                    // Battery output this cycle: limited by output rate and stored power
                                                    PowerValue.BatteryTotalPower += Mathf.Min(MaxOutput, CurrentPower);
                                                }

                                                Banks.Add(InFo.UniqueID);
                                            }
                                            else
                                              if (Type == PowerItem.PowerItemTypes.Generator)
                                            {
                                                // Generators without fuel cannot produce power, ignore them
                                                if (CurrentFuel == 0)
                                                {
                                                    continue;
                                                }

                                                using (PowerValue.AcquireLock())
                                                {
                                                    // Generator output right now: limited by output rate and buffered power
                                                    PowerValue.GeneratorTotalPower += Mathf.Min(MaxOutput, CurrentPower);
                                                    // Generator output if it runs at full rated output
                                                    PowerValue.ExpectedGeneratorTotalPower += MaxOutput;
                                                }

                                                Generators.Add(InFo.UniqueID);
                                            }
                                        }
                                    }
                                }

                                PowerValue = Power[UniqueID];

                                if (PowerValue != null)
                                {
                                    int B_P, G_P, G_B_P, EG_P, EG_B_P;

                                    using (PowerValue.AcquireLock())
                                    {
                                        B_P = PowerValue.BatteryTotalPower;
                                        G_P = PowerValue.GeneratorTotalPower;
                                        G_B_P = PowerValue.GeneratorTotalPower + PowerValue.BatteryTotalPower;
                                        EG_P = PowerValue.ExpectedGeneratorTotalPower;
                                        EG_B_P = PowerValue.ExpectedGeneratorTotalPower + PowerValue.BatteryTotalPower;
                                    }

                                    if (UsedPower > 0)
                                    {
                                        Debug.Log("B_P:" + B_P);
                                        Debug.Log("G_P:" + G_P);
                                        Debug.Log("G_B_P:" + G_B_P);
                                        Debug.Log("EG_P:" + EG_P);
                                        Debug.Log("EG_B_P:" + EG_B_P);
                                        Debug.Log("UsedPower:" + UsedPower);
                                    }

                                    if (B_P >= (UsedPower * 2))
                                    {
                                        // Batteries alone are enough: shut down every generator
                                        for (int ir = 0; ir < Generators.Count; ir++)
                                        {
                                            long QueryID = Generators[ir];

                                            // Device state must be changed on the Unity main thread
                                            UnitySelfTrd(() =>
                                            {
                                                if (Pool.ContainsKey(QueryID))
                                                {
                                                    if (Pool[QueryID] != null && Pool[QueryID] is PowerGenerator)
                                                    {
                                                        var Generator = Pool[QueryID] as PowerGenerator;
                                                        // Generator is no longer registered in the power manager
                                                        if (!PowerManager.Instance.PowerItemDictionary.ContainsKey(Generator.Position))
                                                        {
                                                            return;
                                                        }
                                                        // Auto shutdown
                                                        Generator.IsOn = false;
                                                    }
                                                }
                                            }, true);
                                        }

                                        using (PowerValue.AcquireLock())
                                        {
                                            PowerValue.Power = B_P;
                                            PowerValue.SupplyMode = PowerSupplyMode.B_P;
                                        }
                                    }
                                    else
                                    if (G_P >= UsedPower)
                                    {
                                        // Generators' current buffered output alone covers the demand,
                                        // no extra generation needed
                                        using (PowerValue.AcquireLock())
                                        {
                                            PowerValue.Power = G_P;
                                            PowerValue.SupplyMode = PowerSupplyMode.G_P;
                                        }
                                    }
                                    else
                                    if (G_B_P >= UsedPower && B_P > 0)
                                    {
                                        // Batteries + generators' current buffered output already cover the demand,
                                        // no extra generation needed
                                        using (PowerValue.AcquireLock())
                                        {
                                            PowerValue.Power = G_B_P;
                                            PowerValue.SupplyMode = PowerSupplyMode.G_B_P;
                                        }
                                    }
                                    else
                                    if (EG_P >= UsedPower)
                                    {
                                        // Generators at full rated output alone would cover the demand:
                                        // turn them on and raise their output (on the Unity main thread)
                                        for (int ir = 0; ir < Generators.Count; ir++)
                                        {
                                            long QueryID = Generators[ir];

                                            UnitySelfTrd(() =>
                                            {
                                                if (Pool.ContainsKey(QueryID))
                                                {
                                                    if (Pool[QueryID] != null && Pool[QueryID] is PowerGenerator)
                                                    {
                                                        var Generator = Pool[QueryID] as PowerGenerator;
                                                        // Generator is no longer registered in the power manager
                                                        if (!PowerManager.Instance.PowerItemDictionary.ContainsKey(Generator.Position))
                                                        {
                                                            return;
                                                        }

                                                        // Fuel may have run out since the statistics were collected
                                                        if (Generator.CurrentFuel > 0)
                                                        {
                                                            if (!Generator.IsOn)
                                                            {
                                                                // Smart start: turn the generator on, then raise its output
                                                                Generator.IsOn = true;
                                                                Generator.TickPowerGeneration();
                                                            }
                                                            else
                                                            {
                                                                // Already running: burn fuel to raise its buffered power
                                                                Generator.TickPowerGeneration();
                                                            }
                                                        }
                                                    }
                                                }
                                            }, true);
                                        }

                                        using (PowerValue.AcquireLock())
                                        {
                                            PowerValue.Power = G_P;
                                            PowerValue.SupplyMode = PowerSupplyMode.EG_P;
                                        }
                                    }
                                    else
                                    if (EG_B_P >= UsedPower && B_P > 0)
                                    {
                                        // Current supply is not enough, but batteries + generators at full rated output would be:
                                        // turn generators on and raise their output (on the Unity main thread)
                                        for (int ir = 0; ir < Generators.Count; ir++)
                                        {
                                            long QueryID = Generators[ir];

                                            UnitySelfTrd(() =>
                                            {
                                                if (Pool.ContainsKey(QueryID))
                                                {
                                                    if (Pool[QueryID] != null && Pool[QueryID] is PowerGenerator)
                                                    {
                                                        var Generator = Pool[QueryID] as PowerGenerator;
                                                        // Generator is no longer registered in the power manager
                                                        if (!PowerManager.Instance.PowerItemDictionary.ContainsKey(Generator.Position))
                                                        {
                                                            return;
                                                        }

                                                        if (Generator.CurrentFuel > 0)
                                                        {
                                                            if (!Generator.IsOn)
                                                            {
                                                                Generator.IsOn = true;
                                                            }
                                                            else
                                                            {
                                                                // Already running: burn fuel to raise its buffered power
                                                                Generator.TickPowerGeneration();
                                                            }
                                                        }
                                                    }
                                                }
                                            }, true);
                                        }

                                        using (PowerValue.AcquireLock())
                                        {
                                            PowerValue.Power = G_B_P;
                                            PowerValue.SupplyMode = PowerSupplyMode.EG_B_P;
                                        }
                                    }
                                    else
                                    {
                                        // Power shortage: even batteries + generators at full rated output cannot cover the demand.
                                        // Turn every generator on and keep raising its output;
                                        // batteries are already counted in the supply
                                        for (int ir = 0; ir < Generators.Count; ir++)
                                        {
                                            long QueryID = Generators[ir];

                                            UnitySelfTrd(() =>
                                            {
                                                if (Pool.ContainsKey(QueryID))
                                                {
                                                    if (Pool[QueryID] != null && Pool[QueryID] is PowerGenerator)
                                                    {
                                                        var Generator = Pool[QueryID] as PowerGenerator;
                                                        // Generator is no longer registered in the power manager
                                                        if (!PowerManager.Instance.PowerItemDictionary.ContainsKey(Generator.Position))
                                                        {
                                                            return;
                                                        }

                                                        if (Generator.CurrentFuel > 0)
                                                        {
                                                            // Smart start: turn the generator on if it was shut down
                                                            if (!Generator.IsOn)
                                                            {
                                                                Generator.IsOn = true;
                                                            }

                                                            // Burn fuel to raise buffered power
                                                            Generator.TickPowerGeneration();
                                                        }
                                                    }
                                                }
                                            }, true);
                                        }

                                        using (PowerValue.AcquireLock())
                                        {
                                            PowerValue.Power = G_B_P;
                                            PowerValue.SupplyMode = PowerSupplyMode.EG_B_P;
                                        }
                                    }

                                    if (UsedPower > 0)
                                    {
                                        using (PowerValue.AcquireLock())
                                        {
                                            Debug.Log("SupplyMode:" + PowerValue.SupplyMode.ToString());
                                        }
                                    }
                                }
                            }

                        }
                        catch (Exception Ex)
                        {
                            // Never let an exception kill the power thread
                            Log.Exception(Ex);
                        }
                    }
                });

                PowerTrd.Start();
            }
        }

        public bool UnitySelfTrd(Action Work, bool IsAsync = false, int TimeoutMs = 5000)
        {
            if (Work == null) return false;

            if (System.Environment.CurrentManagedThreadId == UnityThreadId)
            {
                try
                {
                    Work.Invoke();
                }
                catch (Exception E)
                {
                    Log.Exception(E);
                    return false;
                }
                return true;
            }

            if (IsAsync)
            {
                TrdJobs.Enqueue(() =>
                {
                    try
                    {
                        Work.Invoke();
                    }
                    catch (Exception E)
                    {
                        Log.Exception(E);
                    }
                });
                return true;
            }

            using (var DoneEvent = new System.Threading.ManualResetEventSlim(false))
            {
                bool IsCancelled = false;
                bool Success = false;

                TrdJobs.Enqueue(() =>
                {
                    if (IsCancelled) return;

                    try
                    {
                        Work.Invoke();
                        Success = true;
                    }
                    catch (Exception E)
                    {
                        Log.Exception(E);
                        Success = false;
                    }
                    finally
                    {
                        try
                        {
                            DoneEvent.Set();
                        }
                        catch (ObjectDisposedException) { }
                    }
                });

                if (!DoneEvent.Wait(TimeoutMs))
                {
                    IsCancelled = true;
                    return false;
                }

                return Success;
            }
        }
        public void Update()
        {
            while (TrdJobs.TryDequeue(out Action Action))
            {
                Action?.Invoke();
            }
        }

        public int UpdateArray(PowerSource Device)
        {
            if (Device != null)
            {
                if (Device.PowerItemType == PowerItem.PowerItemTypes.BatteryBank || Device.PowerItemType == PowerItem.PowerItemTypes.Generator)
                {
                    if (Device == null)
                    {
                        return -1;
                    }
                    // If there is no parent device, the device itself is the root
                    PowerItem Root = Device.Root ?? Device;

                    var Item = new RootItem(Root);
                    if (!Devices.ContainsKey(Item.UniqueID))
                    {
                        if (Devices.TryAdd(Item.UniqueID, Item))
                        {
                            return 1;
                        }
                    }
                    else
                    {
                        return 0;
                    }
                }
            }
            return -1;
        }

        public static int CalculateSubtreePowerRequired(PowerItem Item)
        {
            if (Item == null) return 0;

            int TotalRequired = 0;

            if (Item is PowerConsumerToggle ConsumerToggle)
            {
                if (ConsumerToggle.isToggled)
                {
                    TotalRequired += Item.RequiredPower;
                }
            }
            else if (Item is PowerTrigger Trigger)
            {
                if (Trigger.IsActive)
                {
                    TotalRequired += Item.RequiredPower;
                }
            }
            else if(Item is PowerGenerator)
            {
                if((Item as PowerGenerator).CurrentFuel > 0)
                TotalRequired += 1;
            }
            else 
            {
                TotalRequired += Item.RequiredPower;
            }

            if (!Item.PowerChildren())
            {
                return TotalRequired;
            }

            for (int i = 0; i < Item.Children.Count; i++)
            {
                PowerItem Child = Item.Children[i];

                if (Child is PowerSource) continue;

                TotalRequired += CalculateSubtreePowerRequired(Child);
            }

            return TotalRequired;
        }

        public void GeneratorHandleSendPower(PowerGenerator Item)
        {
            if (!Item.IsOn)
            {
                return;
            }

            if (Item.CurrentPower < Item.MaxPower)
            {
                Item.TickPowerGeneration();
            }
            else if (Item.CurrentPower > Item.MaxPower)
            {
                Item.CurrentPower = Item.MaxPower;
            }

            if (Item.ShouldAutoTurnOff())
            {
                Item.CurrentPower = 0;
                Item.IsOn = false;
            }

            if (Item.hasChangesLocal)
            {
                Item.LastPowerUsed = 0;
                ushort num = (ushort)Mathf.Min(Item.MaxOutput, Item.CurrentPower);
                ushort power = num;
                _ = GameManager.Instance.World;
                for (int i = 0; i < Item.Children.Count; i++)
                {
                    num = power;
                    Item.Children[i].HandlePowerReceived(ref power);
                    Item.LastPowerUsed += (ushort)(num - power);
                }
            }

            Item.CurrentPower -= Item.LastPowerUsed;
        }
        public void BatteryBankHandleSendPower(PowerBatteryBank Item)
        {
            if (!Item.IsOn || Item.ParentPowering)
            {
                return;
            }

            if (Item.CurrentPower < Item.MaxPower)
            {
                Item.TickPowerGeneration();
            }
            else if (Item.CurrentPower > Item.MaxPower)
            {
                Item.CurrentPower = Item.MaxPower;
            }

            if (Item.CurrentPower <= 0)
            {
                Item.CurrentPower = 0;
                if (Item.isPowered)
                {
                    Item.HandleDisconnect();
                    Item.hasChangesLocal = true;
                }
            }
            else
            {
                Item.isPowered = true;
            }

            if (Item.hasChangesLocal)
            {
                Item.LastPowerUsed = 0;
                ushort num = (ushort)Mathf.Min(Item.MaxOutput, Item.CurrentPower);
                ushort power = num;
                _ = GameManager.Instance.World;
                for (int i = 0; i < Item.Children.Count; i++)
                {
                    num = power;
                    Item.Children[i].HandlePowerReceived(ref power);
                    Item.LastPowerUsed += (ushort)(num - power);
                }
            }

            Item.CurrentPower -= (ushort)Mathf.Min(Item.CurrentPower, Item.LastPowerUsed);
        }
    }
}

