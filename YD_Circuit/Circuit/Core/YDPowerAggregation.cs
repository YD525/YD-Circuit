using System.Collections.Concurrent;
using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using System.Linq;
using static PowerItem;

public class YDPowerAggregation
{
    public static YDPowerAggregation Instance = new YDPowerAggregation();

    public ThreadSafeList<PowerItem> Devices = new ThreadSafeList<PowerItem>();
    public ConcurrentDictionary<PowerItem, MainPower> Power = new ConcurrentDictionary<PowerItem, MainPower>();

    public Thread PowerTrd = null;
    private ConcurrentQueue<Action> TrdJobs = new ConcurrentQueue<Action>();

    public void CheckExpired(ThreadSafeList<PowerItem> DevicesRef)
    {
        if (DevicesRef == null) return;

        UnitySelfTrd(() =>
        {
            List<PowerItem> ExpiredRoots = new List<PowerItem>();

            DevicesRef.RemoveAll(Item =>
            {
                bool Expired = false;

                if (Item == null)
                {
                    Expired = true;
                }
                else
                if (Item.Root != null)
                {
                    Expired = true;
                }
                else
                {
                    if (!PowerManager.Instance.PowerItemDictionary.ContainsKey(Item.Position))
                    {
                        Expired = true;
                    }
                }

                if (Expired)
                {
                    ExpiredRoots.Add(Item);
                }

                return false;
            });

            for (int i = 0; i < ExpiredRoots.Count; i++)
            {
                var Key = ExpiredRoots[i];

                while (Power.ContainsKey(Key))
                {
                    if (Power.TryRemove(Key, out MainPower Value))
                    {
                        break;
                    }
                    Thread.Yield();
                }
            }
        });
    }

    public static List<PowerItem> GetAllConnectedNodes(PowerItem StartNode)
    {
        List<PowerItem> Result = new List<PowerItem>();
        if (StartNode == null) return Result;

        HashSet<PowerItem> Visited = new HashSet<PowerItem>();
        TraverseConnectedNodes(StartNode, Visited, Result);

        return Result;
    }

    private static void TraverseConnectedNodes(PowerItem CurrentNode, HashSet<PowerItem> Visited, List<PowerItem> Result)
    {
        if (CurrentNode == null) return;

        if (!Visited.Add(CurrentNode)) return;

        Result.Add(CurrentNode);

        if (CurrentNode.PowerChildren())
        {
            for (int i = 0; i < CurrentNode.Children.Count; i++)
            {
                PowerItem Child = CurrentNode.Children[i];

                if (Child is PowerSource) continue;

                TraverseConnectedNodes(Child, Visited, Result);
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
                        CheckExpired(Devices);

                        List<PowerItem> All = Devices.GetAllSnapshot();

                        for (int i = 0; i < All.Count; i++)
                        {
                            List<PowerSource> Banks = new List<PowerSource>();
                            List<PowerSource> Generators = new List<PowerSource>();

                            List<PowerItem> Nodes = GetAllConnectedNodes(All[i]);

                            PowerItem Root = All[i];

                            if (Root == null)
                            {
                                continue;
                            }

                            if (!Power.ContainsKey(Root))
                            {
                                Power.TryAdd(Root, new MainPower());
                            }

                            var PowerValue = Power[Root];

                            using (PowerValue.AcquireLock())
                            {
                                PowerValue.BatteryTotalPower = 0;
                                PowerValue.GeneratorTotalPower = 0;
                                PowerValue.ExpectedGeneratorTotalPower = 0;
                            }

                            for (int ir = 0; ir < Nodes.Count; ir++)
                            {
                                if (Nodes[ir] != null)
                                    if (Nodes[ir] is PowerSource)
                                    {
                                        PowerSource Device = (PowerSource)Nodes[ir];

                                        ushort MaxOutput = 0;
                                        ushort CurrentPower = 0;
                                        ushort CurrentFuel = 0;

                                        PowerItemTypes Type = PowerItemTypes.None;

                                        bool IsNull = true;

                                        UnitySelfTrd(() =>
                                        {
                                            MaxOutput = Device.MaxOutput;
                                            CurrentPower = Device.CurrentPower;
                                            Type = Device.PowerItemType;

                                            if (Device != null)
                                            {
                                                IsNull = false;

                                                if (Device is PowerGenerator)
                                                {
                                                    CurrentFuel = ((PowerGenerator)Device).CurrentFuel;
                                                }
                                            }
                                        });

                                        if (!IsNull)
                                        {
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
                                                   PowerValue.BatteryTotalPower += Mathf.Min(MaxOutput,CurrentPower);
                                                }

                                                Banks.Add(Device);
                                            }
                                            else
                                            {
                                                // Generators without fuel cannot produce power, ignore them
                                                if (CurrentFuel == 0)
                                                {
                                                    continue;
                                                }

                                                using (PowerValue.AcquireLock())
                                                {
                                                    // Generator output right now: limited by output rate and buffered power
                                                    PowerValue.GeneratorTotalPower += Mathf.Min(Device.MaxOutput,Device.CurrentPower);
                                                    // Generator output if it runs at full rated output
                                                    PowerValue.ExpectedGeneratorTotalPower += Device.MaxOutput;
                                                }
                                                  
                                                Generators.Add(Device);
                                            }
                                        }
                                    }

                            }

                            int UsedPower = CalculateSubtreePowerRequired(Root);

                            int B_P,G_P,G_B_P,EG_P, EG_B_P;


                            using (PowerValue.AcquireLock())
                            {
                                B_P = PowerValue.BatteryTotalPower;
                                G_P = PowerValue.GeneratorTotalPower;
                                G_B_P = PowerValue.GeneratorTotalPower + PowerValue.BatteryTotalPower;
                                EG_P = PowerValue.ExpectedGeneratorTotalPower;
                                EG_B_P = PowerValue.ExpectedGeneratorTotalPower + PowerValue.BatteryTotalPower;
                            }

                            if (B_P >= (UsedPower * 2))
                            {
                                // Batteries alone are enough: shut down every generator
                                for (int ir = 0; ir < Generators.Count; ir++)
                                {
                                    // Copy to a local so each queued action captures its own generator
                                    PowerSource Generator = Generators[ir];

                                    // Device state must be changed on the Unity main thread
                                    UnitySelfTrd(() =>
                                    {
                                        // Generator was destroyed
                                        if (Generator == null)
                                        {
                                            return;
                                        }

                                        // Generator is no longer registered in the power manager
                                        if (!PowerManager.Instance.PowerItemDictionary.ContainsKey(Generator.Position))
                                        {
                                            return;
                                        }

                                        // Auto shutdown
                                        Generator.IsOn = false;
                                    },true);
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
                                    PowerSource Generator = Generators[ir];

                                    UnitySelfTrd(() =>
                                    {
                                        // Generator was destroyed
                                        if (Generator == null)
                                        {
                                            return;
                                        }

                                        // Generator is no longer registered in the power manager
                                        if (!PowerManager.Instance.PowerItemDictionary.ContainsKey(Generator.Position))
                                        {
                                            return;
                                        }

                                        // Fuel may have run out since the statistics were collected
                                        if (((PowerGenerator)Generator).CurrentFuel > 0)
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
                                    },true);
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
                                    PowerSource Generator = Generators[ir];

                                    UnitySelfTrd(() =>
                                    {
                                        // Generator was destroyed
                                        if (Generator == null)
                                        {
                                            return;
                                        }

                                        // Generator is no longer registered in the power manager
                                        if (!PowerManager.Instance.PowerItemDictionary.ContainsKey(Generator.Position))
                                        {
                                            return;
                                        }

                                        // Fuel may have run out since the statistics were collected
                                        if (((PowerGenerator)Generator).CurrentFuel > 0)
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
                                    },true);
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
                                    PowerSource Generator = Generators[ir];

                                    UnitySelfTrd(() =>
                                    {
                                        // Generator was destroyed
                                        if (Generator == null)
                                        {
                                            return;
                                        }

                                        // Generator is no longer registered in the power manager
                                        if (!PowerManager.Instance.PowerItemDictionary.ContainsKey(Generator.Position))
                                        {
                                            return;
                                        }

                                        // Fuel may have run out since the statistics were collected
                                        if (((PowerGenerator)Generator).CurrentFuel > 0)
                                        {
                                            // Smart start: turn the generator on if it was shut down
                                            if (!Generator.IsOn)
                                            {
                                                Generator.IsOn = true;
                                            }

                                            // Burn fuel to raise buffered power
                                            Generator.TickPowerGeneration();
                                        }
                                    },true);
                                }

                                using (PowerValue.AcquireLock())
                                {
                                    PowerValue.Power = G_B_P;
                                    PowerValue.SupplyMode = PowerSupplyMode.EG_B_P;
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

            TrdJobs.Enqueue(() =>
            {
                if (IsCancelled) return;

                try
                {
                    Work.Invoke();
                }
                catch (Exception E)
                {
                    Log.Exception(E);
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

            return true;
        }
    }

    private void Update()
    {
        while (TrdJobs.TryDequeue(out Action Action))
        {
            Action?.Invoke();
        }
    }

    public void Update(PowerSource Device)
    {
        if (Device == null)
        {
            Update();
        }
        else
        {
            if (Device.PowerItemType == PowerItem.PowerItemTypes.BatteryBank || Device.PowerItemType == PowerItem.PowerItemTypes.Generator)
            {
                if (Device == null)
                {
                    return;
                }

                // If there is no parent device, the device itself is the root
                PowerItem Root = Device.Root ?? Device;
                if (!Devices.Contains(Root))
                {
                    Devices.Add(Root);
                }
            }
        }
    }

    public static ushort CalculateSubtreePowerRequired(PowerItem Item)
    {
        if (Item == null) return 0;

        ushort TotalRequired = 0;

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
