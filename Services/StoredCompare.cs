using Coflnet.Sky.PlayerState.Models;
using System.Linq;
using MongoDB.Driver;
using System.Collections.Generic;
using System;

namespace Coflnet.Sky.PlayerState.Services
{
    public class StoredCompare : IEqualityComparer<StoredItem>
    {
        public bool Equals(StoredItem? x, StoredItem? y)
        {
            return x != null && y != null && x.ExtraAttributes.Equals(y.ExtraAttributes)
               && (x.Enchantments == y.Enchantments || y.Enchantments?.Count == x.Enchantments?.Count && y.Enchantments != null && x.Enchantments != null && !x.Enchantments.Except(y.Enchantments).Any())
               && x.Color == y.Color
               && x.Tag == y.Tag;
        }

        int IEqualityComparer<StoredItem>.GetHashCode(StoredItem obj)
        {
            return HashCode.Combine(obj.ExtraAttributes, obj.Tag);
        }
    }
}