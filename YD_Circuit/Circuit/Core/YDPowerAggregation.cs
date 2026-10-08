using System.Collections.Concurrent;
using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using System.Linq;

public class YDPowerAggregation
{
    public static YDPowerAggregation Instance = new YDPowerAggregation();

    public object PowerLock = new object();
    public ThreadSafeList<PowerItem> Devices = new ThreadSafeList<PowerItem>();

    public Thread PowerTrd = null;

    public ConcurrentDictionary<PowerItem, MainPower> Power = new ConcurrentDictionary<PowerItem, MainPower>();

    public ConcurrentQueue<Action> MainThreadActions = new ConcurrentQueue<Action>();

    public void CheckExpired(ThreadSafeList<PowerItem> DevicesRef)
    {
        if (DevicesRef == null) return;

        UnitySelfTrd(() =>
        {
            DevicesRef.RemoveAll(Item =>
            {
                if (Item == null)
                    return true;

                if (Item.Root != null)
                    return true;

                if (!PowerManager.Instance.PowerItemDictionary.ContainsKey(Item.Position))
                    return true;

                return false;
            });
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

    public void Start()
    {
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
                        lock (PowerLock)
                        {
                            CheckExpired(Devices);

                            List<PowerItem> All = Devices.GetAllSnapshot();

                            List<PowerSource> Banks = new List<PowerSource>();
                            List<PowerSource> Generators = new List<PowerSource>();
                            PowerItem Root = null;

                            for (int i = 0; i < All.Count; i++)
                            {
                                List<PowerItem> Sources = GetAllConnectedNodes(All[i]);

                                Root = All[i];

                                if (Root == null)
                                {
                                    continue;
                                }

                                if (!Power.ContainsKey(Root))
                                {
                                    Power.TryAdd(Root, new MainPower());
                                }

                                Power[Root].BatteryTotalPower = 0;
                                Power[Root].GeneratorTotalPower = 0;
                                Power[Root].ExpectedGeneratorTotalPower = 0;

                                for (int ir = 0; ir < Sources.Count; ir++)
                                {
                                    if (Sources[ir] != null)
                                    if (Sources[ir] is PowerSource)
                                    {
                                        PowerSource Device = (PowerSource)Sources[ir];

                                        if (Device != null)
                                        {
                                            if (Device.PowerItemType == PowerItem.PowerItemTypes.BatteryBank)
                                            {
                                                // Empty batteries cannot supply anything
                                                if (Device.CurrentPower == 0)
                                                {
                                                    continue;
                                                }

                                                // Battery output this cycle: limited by output rate and stored power
                                                Power[Root].BatteryTotalPower += Mathf.Min(
                                                   Device.MaxOutput,
                                                   Device.CurrentPower);

                                                Banks.Add(Device);
                                            }
                                            else
                                            {
                                                // Generators without fuel cannot produce power, ignore them
                                                if (((PowerGenerator)Device).CurrentFuel == 0)
                                                {
                                                    continue;
                                                }

                                                // Generator output right now: limited by output rate and buffered power
                                                Power[Root].GeneratorTotalPower += Mathf.Min(
                                                    Device.MaxOutput,
                                                    Device.CurrentPower);

                                                // Generator output if it runs at full rated output
                                                Power[Root].ExpectedGeneratorTotalPower += Device.MaxOutput;

                                                Generators.Add(Device);
                                            }
                                        }
                                    }
                                  
                                }

                                int UsedPower = CalculateSubtreePowerRequired(Root);

                                int B_P = Power[Root].BatteryTotalPower;
                                int G_P = Power[Root].GeneratorTotalPower;
                                int G_B_P = Power[Root].GeneratorTotalPower + Power[Root].BatteryTotalPower;

                                int EG_P = Power[Root].ExpectedGeneratorTotalPower;
                                int EG_B_P = Power[Root].ExpectedGeneratorTotalPower + Power[Root].BatteryTotalPower;

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
                                        });
                                    }

                                    Power[Root].Power = B_P;
                                    Power[Root].SupplyMode = PowerSupplyMode.B_P;
                                }
                                else
                                if (G_P >= UsedPower)
                                {
                                    // Generators' current buffered output alone covers the demand,
                                    // no extra generation needed

                                    Power[Root].Power = G_P;
                                    Power[Root].SupplyMode = PowerSupplyMode.G_P;
                                }
                                else
                                if (G_B_P >= UsedPower && B_P > 0)
                                {
                                    // Batteries + generators' current buffered output already cover the demand,
                                    // no extra generation needed

                                    Power[Root].Power = G_B_P;
                                    Power[Root].SupplyMode = PowerSupplyMode.G_B_P;
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
                                        });
                                    }

                                    Power[Root].Power = G_P;
                                    Power[Root].SupplyMode = PowerSupplyMode.EG_P;
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
                                        });
                                    }

                                    Power[Root].Power = G_B_P;
                                    Power[Root].SupplyMode = PowerSupplyMode.EG_B_P;
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
                                        });
                                    }

                                    Power[Root].Power = G_B_P;
                                    Power[Root].SupplyMode = PowerSupplyMode.EG_B_P;
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

    public void UnitySelfTrd(Action Work)
    {
        MainThreadActions.Enqueue(() =>
        {
            try
            {
                Work.Invoke();
            }
            catch (System.Exception e)
            {
                Log.Exception(e);
            }
        });

        while (MainThreadActions.Contains(Work))
        {
            Thread.Sleep(10);
        }
    }

    public void Update()
    {
        while (MainThreadActions.TryDequeue(out Action Action))
        {
            Action.Invoke();
            //Thread.Sleep(10);
        }
    }
    public void UPDate(PowerSource Device = null)
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
