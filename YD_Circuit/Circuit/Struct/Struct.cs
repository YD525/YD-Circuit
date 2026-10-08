using System.Collections.Generic;
using System;
using static PowerItem;
using System.Runtime.InteropServices;

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
    private readonly object _PowerLock = new object();

    public int Power;
    public ushort RequiredPower;
    public int BatteryTotalPower;
    public int GeneratorTotalPower;
    public int ExpectedGeneratorTotalPower;

    public ushort Used;
    public ushort LastDelivered;
    public bool Starved;
    public PowerSupplyMode SupplyMode = PowerSupplyMode.NULL;

    public LockScope AcquireLock()
    {
        return new LockScope(_PowerLock);
    }

    public readonly struct LockScope : System.IDisposable
    {
        private readonly object _LockObj;

        public LockScope(object LockObj)
        {
            _LockObj = LockObj;
            System.Threading.Monitor.Enter(_LockObj);
        }

        public void Dispose()
        {
            System.Threading.Monitor.Exit(_LockObj);
        }
    }
}

public class PowerSourceInFo
{
    public long UniqueID = 0;
    public PowerItem Object;

    public ushort MaxOutput = 0;
    public ushort CurrentPower = 0;
    public ushort CurrentFuel = 0;

    public bool IsRoot = false;

    public PowerItemTypes Type = PowerItemTypes.None;
}

public class IDGen
{
    public static long GetUniqueID64(PowerItem Item)
    {
        if (Item == null) return 0;

        long Offset = 32768;
        long X = (long)Item.Position.x + Offset;
        long Y = (long)Item.Position.y + Offset;
        long Z = (long)Item.Position.z + Offset;
        long BlockId = (long)Item.BlockID;

        return (BlockId << 48) | ((Y & 0xFFFF) << 32) | ((X & 0xFFFF) << 16) | (Z & 0xFFFF);
    }
}

public class RootItem
{
    public PowerItem Object;
    public int BlockID = 0;
    public long UniqueID = 0;
    public RootItem(PowerItem Item)
    {
        UniqueID = IDGen.GetUniqueID64(Item);
        BlockID = Item.BlockID;
        Object = Item;
    }
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
