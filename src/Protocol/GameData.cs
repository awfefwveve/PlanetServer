using System;
using System.Collections.Generic;

namespace Planet.Protocol
{
    // Static game data shared by server (rules) and client (names, descriptions, icons).
    // Original portfolio content; not taken from any existing game.
    public enum ItemKind { Consumable, Equipment, Material }

    public sealed class ItemDefinition
    {
        public string Id { get; }
        public string Name { get; }
        public string Description { get; }
        public ItemKind Kind { get; }
        public int MaxStack { get; }
        // Equipment only: "weapon", "hat", "body", "shoes".
        public string EquipSlot { get; }
        public int RequiredLevel { get; }
        public int Attack { get; }
        public int Defense { get; }
        // Consumable only.
        public int HealHp { get; }
        public int HealMp { get; }
        public int SellPrice { get; }
        // Client icon hint: a symbol and a tint (r,g,b in 0..1).
        public string Glyph { get; }
        public float[] Tint { get; }
        public ItemDefinition(string id,string name,string description,ItemKind kind,int maxStack,string glyph,float[] tint,
            string equipSlot="",int requiredLevel=1,int attack=0,int defense=0,int healHp=0,int healMp=0,int sellPrice=1)
        {
            Id=id;Name=name;Description=description;Kind=kind;MaxStack=maxStack;Glyph=glyph;Tint=tint;
            EquipSlot=equipSlot;RequiredLevel=requiredLevel;Attack=attack;Defense=defense;HealHp=healHp;HealMp=healMp;SellPrice=sellPrice;
        }
    }

    public static class ItemCatalog
    {
        public const int BagCapacity = 24;
        public static readonly string[] EquipSlots = { "weapon", "hat", "body", "shoes" };
        private static readonly Dictionary<string, ItemDefinition> items = new Dictionary<string, ItemDefinition>();
        public static IEnumerable<ItemDefinition> All => items.Values;
        static ItemCatalog()
        {
            Add(new ItemDefinition("potion_red","들꽃 물약","들꽃 꿀을 졸여 만든 물약. HP를 120 회복해요.",ItemKind.Consumable,100,"♥",new[]{.95f,.35f,.42f},healHp:120,sellPrice:5));
            Add(new ItemDefinition("potion_blue","이슬 물약","새벽 이슬을 모은 물약. MP를 50 회복해요.",ItemKind.Consumable,100,"♦",new[]{.35f,.62f,.98f},healMp:50,sellPrice:6));
            Add(new ItemDefinition("slime_jelly","말랑 젤리","슬라임이 남긴 말랑한 젤리. 재료로 쓰여요.",ItemKind.Material,200,"●",new[]{.55f,.9f,.6f},sellPrice:2));
            Add(new ItemDefinition("twig_sword","나뭇가지 검","단단한 나뭇가지를 다듬은 연습용 검.",ItemKind.Equipment,1,"†",new[]{.78f,.6f,.38f},equipSlot:"weapon",attack:1,sellPrice:10));
            Add(new ItemDefinition("star_blade","별조각 검","별조각을 박아 넣은 가벼운 검.",ItemKind.Equipment,1,"✦",new[]{.98f,.85f,.4f},equipSlot:"weapon",requiredLevel:3,attack:3,sellPrice:60));
            Add(new ItemDefinition("leaf_cap","나뭇잎 모자","커다란 나뭇잎을 엮은 모자.",ItemKind.Equipment,1,"▲",new[]{.45f,.78f,.4f},equipSlot:"hat",defense:1,sellPrice:12));
            Add(new ItemDefinition("cotton_vest","솜털 조끼","포근한 솜털로 누빈 조끼.",ItemKind.Equipment,1,"■",new[]{.96f,.92f,.82f},equipSlot:"body",requiredLevel:2,defense:2,sellPrice:20));
            Add(new ItemDefinition("cloud_boots","구름 장화","밟으면 폭신한 구름색 장화.",ItemKind.Equipment,1,"◆",new[]{.75f,.85f,1f},equipSlot:"shoes",defense:1,sellPrice:15));
        }
        private static void Add(ItemDefinition item) => items.Add(item.Id, item);
        public static ItemDefinition Find(string id) => id != null && items.TryGetValue(id, out var item) ? item : null;
    }

    public enum SkillTarget { Single, Area, Self }

    public sealed class SkillDefinition
    {
        public string Id { get; }
        public string Name { get; }
        public string Description { get; }
        public SkillTarget Target { get; }
        public int RequiredLevel { get; }
        public int MaxLevel { get; }
        public int MpCost { get; }
        public double Cooldown { get; }
        public float Range { get; }
        public string Glyph { get; }
        public float[] Tint { get; }
        public SkillDefinition(string id,string name,string description,SkillTarget target,int requiredLevel,int maxLevel,int mpCost,double cooldown,float range,string glyph,float[] tint)
        {Id=id;Name=name;Description=description;Target=target;RequiredLevel=requiredLevel;MaxLevel=maxLevel;MpCost=mpCost;Cooldown=cooldown;Range=range;Glyph=glyph;Tint=tint;}
        // Damage multiplier (x100) for attack skills, heal percent of max HP for self skills.
        public int Power(int level)
        {
            switch (Id)
            {
                case "pebble_strike": return 200 + 50 * level;
                case "petal_whirl": return 150 + 30 * level;
                case "sprout_heal": return 20 + 4 * level;
                default: return 100;
            }
        }
    }

    public static class SkillCatalog
    {
        private static readonly Dictionary<string, SkillDefinition> skills = new Dictionary<string, SkillDefinition>();
        public static IEnumerable<SkillDefinition> All => skills.Values;
        static SkillCatalog()
        {
            Add(new SkillDefinition("pebble_strike","조약돌 내려치기","한 대상을 힘껏 내려쳐 공격력의 2.5배~4.5배 피해를 줘요.",SkillTarget.Single,1,5,8,3,1.8f,"✊",new[]{.98f,.62f,.3f}));
            Add(new SkillDefinition("sprout_heal","새싹 숨결","새싹의 기운으로 최대 HP의 24%~40%를 회복해요.",SkillTarget.Self,2,5,12,10,0,"✿",new[]{.5f,.9f,.55f}));
            Add(new SkillDefinition("petal_whirl","꽃잎 회오리","주변의 모든 몬스터에게 공격력의 1.8배~3배 피해를 줘요.",SkillTarget.Area,3,5,15,6,2.2f,"❀",new[]{.98f,.6f,.85f}));
        }
        private static void Add(SkillDefinition skill) => skills.Add(skill.Id, skill);
        public static SkillDefinition Find(string id) => id != null && skills.TryGetValue(id, out var skill) ? skill : null;
    }
}
