using Planet.Protocol;

namespace Planet.Server;

// Persisted per character inside the account document, so one account save is one atomic
// write (file replace / single PostgreSQL row with revision check). Treat as immutable:
// every rule returns a new instance and never mutates its input.
public sealed record ItemStack(string ItemId,int Count);
public sealed record InventoryData(ItemStack?[] Slots,Dictionary<string,string> Equipped)
{
    public static InventoryData Empty()=>new(new ItemStack?[ItemCatalog.BagCapacity],new());
    public static InventoryData StarterKit()=>InventoryRules.Add(Empty(),new[]{new ItemStack("potion_red",5),new ItemStack("twig_sword",1)}).Inventory!;
}
public sealed record SkillData(Dictionary<string,int> Levels,int Points)
{
    public static SkillData Starting()=>new(new(){{"pebble_strike",1}},0);
}

public static class InventoryRules
{
    // Normalizes data read from storage (old saves have no inventory, capacity may grow).
    public static InventoryData Load(InventoryData? saved)
    {
        if(saved is null)return InventoryData.Empty();
        var slots=new ItemStack?[ItemCatalog.BagCapacity];
        for(int i=0;i<Math.Min(slots.Length,saved.Slots?.Length??0);i++) {
            var s=saved.Slots![i];
            if(s!=null&&ItemCatalog.Find(s.ItemId) is {} def&&s.Count>0)slots[i]=s with {Count=Math.Min(s.Count,def.MaxStack)};
        }
        var equipped=new Dictionary<string,string>();
        foreach(var (slot,item) in saved.Equipped??new())
            if(ItemCatalog.Find(item) is {Kind:ItemKind.Equipment} def&&def.EquipSlot==slot)equipped[slot]=item;
        return new(slots,equipped);
    }
    private static InventoryData Copy(InventoryData inv)=>new((ItemStack?[])inv.Slots.Clone(),new(inv.Equipped));

    // All-or-nothing: either every stack fits (merging into partial stacks first) or nothing changes.
    public static (string Code,InventoryData? Inventory) Add(InventoryData source,IEnumerable<ItemStack> gains)
    {
        var inv=Copy(source);
        foreach(var gain in gains) {
            var def=ItemCatalog.Find(gain.ItemId);if(def is null||gain.Count<1)return ("unknown_item",null);
            int left=gain.Count;
            for(int i=0;i<inv.Slots.Length&&left>0;i++) {
                var s=inv.Slots[i];if(s is null||s.ItemId!=gain.ItemId||s.Count>=def.MaxStack)continue;
                int put=Math.Min(left,def.MaxStack-s.Count);inv.Slots[i]=s with {Count=s.Count+put};left-=put;
            }
            for(int i=0;i<inv.Slots.Length&&left>0;i++) {
                if(inv.Slots[i]!=null)continue;
                int put=Math.Min(left,def.MaxStack);inv.Slots[i]=new(gain.ItemId,put);left-=put;
            }
            if(left>0)return ("bag_full",null);
        }
        return ("ok",inv);
    }
    public static (string Code,InventoryData Inventory,ItemDefinition? Item) TakeOne(InventoryData source,int slot)
    {
        if(slot<0||slot>=source.Slots.Length||source.Slots[slot] is not {} s)return ("empty_slot",source,null);
        var inv=Copy(source);inv.Slots[slot]=s.Count>1?s with {Count=s.Count-1}:null;
        return ("ok",inv,ItemCatalog.Find(s.ItemId));
    }
    public static (string Code,InventoryData Inventory) Equip(InventoryData source,int slot,int level)
    {
        if(slot<0||slot>=source.Slots.Length||source.Slots[slot] is not {} s)return ("empty_slot",source);
        var def=ItemCatalog.Find(s.ItemId);
        if(def is not {Kind:ItemKind.Equipment})return ("not_equipment",source);
        if(level<def.RequiredLevel)return ("level_too_low",source);
        var inv=Copy(source);
        // The item leaves the bag; whatever was worn takes its place (a swap never needs a free slot).
        inv.Slots[slot]=inv.Equipped.TryGetValue(def.EquipSlot,out var worn)?new ItemStack(worn,1):null;
        inv.Equipped[def.EquipSlot]=def.Id;
        return ("ok",inv);
    }
    public static (string Code,InventoryData Inventory) Unequip(InventoryData source,string equipSlot)
    {
        if(!source.Equipped.TryGetValue(equipSlot,out var worn))return ("not_equipped",source);
        int free=Array.FindIndex(source.Slots,s=>s is null);if(free<0)return ("bag_full",source);
        var inv=Copy(source);inv.Equipped.Remove(equipSlot);inv.Slots[free]=new(worn,1);return ("ok",inv);
    }
    // Swap two slots, or merge into the target when both hold the same stackable item.
    public static (string Code,InventoryData Inventory) Move(InventoryData source,int from,int to)
    {
        int n=source.Slots.Length;
        if(from<0||from>=n||to<0||to>=n||from==to)return ("invalid_slot",source);
        if(source.Slots[from] is not {} moving)return ("empty_slot",source);
        var inv=Copy(source);var target=inv.Slots[to];
        if(target!=null&&target.ItemId==moving.ItemId&&ItemCatalog.Find(moving.ItemId) is {MaxStack:>1} def) {
            int put=Math.Min(moving.Count,def.MaxStack-target.Count);
            inv.Slots[to]=target with {Count=target.Count+put};
            inv.Slots[from]=moving.Count-put>0?moving with {Count=moving.Count-put}:null;
        }
        else {inv.Slots[to]=moving;inv.Slots[from]=target;}
        return ("ok",inv);
    }
    public static (string Code,InventoryData Inventory) Discard(InventoryData source,int slot)
    {
        if(slot<0||slot>=source.Slots.Length||source.Slots[slot] is null)return ("empty_slot",source);
        var inv=Copy(source);inv.Slots[slot]=null;return ("ok",inv);
    }
    public static int AttackBonus(InventoryData? inv)=>Load(inv).Equipped.Values.Sum(id=>ItemCatalog.Find(id)?.Attack??0);
    public static int DefenseBonus(InventoryData? inv)=>Load(inv).Equipped.Values.Sum(id=>ItemCatalog.Find(id)?.Defense??0);

    public static Inventory View(InventoryData? data)
    {
        var inv=Load(data);var view=new Inventory{Capacity=inv.Slots.Length};
        for(int i=0;i<inv.Slots.Length;i++)if(inv.Slots[i] is {} s)view.Items.Add(new ItemStackView{Slot=i,ItemId=s.ItemId,Count=s.Count});
        foreach(var slot in ItemCatalog.EquipSlots)if(inv.Equipped.TryGetValue(slot,out var id))view.Equipped.Add(new EquipView{Slot=slot,ItemId=id});
        return view;
    }
}

// Portfolio drop table for slimes. Each entry rolls independently.
public static class DropTables
{
    private static readonly (string Item,double Chance)[] slime=[
        ("slime_jelly",.70),("potion_red",.20),("potion_blue",.10),("leaf_cap",.04),("cloud_boots",.03),("cotton_vest",.02),("star_blade",.01)];
    public static List<ItemStack> Slime(Func<double> roll)
    {
        var drops=new List<ItemStack>();
        foreach(var (item,chance) in slime)if(roll()<chance)drops.Add(new(item,1));
        return drops;
    }
}

public static class SkillRules
{
    public static SkillData Load(SkillData? saved)
    {
        if(saved is null)return SkillData.Starting();
        var levels=new Dictionary<string,int>();
        foreach(var (id,level) in saved.Levels??new())if(SkillCatalog.Find(id) is {} def&&level>0)levels[id]=Math.Min(level,def.MaxLevel);
        return new(levels,Math.Max(0,saved.Points));
    }
    public static SkillData GrantPoints(SkillData? saved,int points)=>Load(saved) with {Points=Load(saved).Points+Math.Max(0,points)};
    public static (string Code,SkillData Skills) Learn(SkillData? saved,string id,int characterLevel)
    {
        var skills=Load(saved);var def=SkillCatalog.Find(id);
        if(def is null)return ("unknown_skill",skills);
        if(characterLevel<def.RequiredLevel)return ("level_too_low",skills);
        int current=skills.Levels.GetValueOrDefault(id);
        if(current>=def.MaxLevel)return ("skill_maxed",skills);
        if(skills.Points<1)return ("no_skill_points",skills);
        var levels=new Dictionary<string,int>(skills.Levels){[id]=current+1};
        return ("ok",new(levels,skills.Points-1));
    }
}
