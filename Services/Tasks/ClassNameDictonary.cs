using System;
using System.Collections.Generic;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>
/// Registry helper mirroring the mod side Coflnet.Sky.Commands.Shared.ClassNameDictonary
/// so TaskCatalog can be ported without changes.
/// </summary>
public class ClassNameDictonary<T> : Dictionary<string, T>
{
    public void Add<TDerived>(string altName = null) where TDerived : T
    {
        var instance = Activator.CreateInstance<TDerived>();
        Add(GetCleardName<TDerived>(), instance);
        if (altName != null)
            Add(altName, instance);
    }

    public static string GetCleardName<TDerived>() where TDerived : T
    {
        return typeof(TDerived).Name.Replace("Command", "").Replace(typeof(T).Name, "").ToLower();
    }
}
