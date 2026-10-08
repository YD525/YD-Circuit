using System.Collections.Generic;
using System;

public class ThreadSafeList<T>
{
    private readonly List<T> _List = new List<T>();
    private readonly object _Lock = new object();
    public bool Contains(T Item)
    {
        lock (_Lock)
        {
            return _List.Contains(Item);
        }
    }
    public void Add(T item)
    {
        lock (_Lock)
        {
            _List.Add(item);
        }
    }

    public bool Remove(T item)
    {
        lock (_Lock)
        {
            return _List.Remove(item);
        }
    }

    public List<T> GetAllSnapshot()
    {
        lock (_Lock)
        {
            return new List<T>(_List);
        }
    }
    public int RemoveAll(Predicate<T> Match)
    {
        lock (_Lock)
        {
            return _List.RemoveAll(Match);
        }
    }
}







public class MainPower
{
    public int Power = 0;
    public ushort RequiredPower = 0;
    public int BatteryTotalPower = 0;
    public int GeneratorTotalPower = 0;
    public int ExpectedGeneratorTotalPower = 0;

    public ushort Used = 0;
    public ushort LastDelivered = 0;
    public bool Starved = false;
    public PowerSupplyMode SupplyMode = PowerSupplyMode.NULL;
}

public enum PowerSupplyMode
{
    NULL,
    B_P,
    G_P,
    G_B_P,
    EG_P,
    EG_B_P
}
