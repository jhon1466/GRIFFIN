namespace Griffin.GameServer.Services.Darkov
{
    /// <summary>An item stack of the Darkov mode: unique id, table id (<c>EsItem</c>), amount and, for weapons and armour, the durability (current, maximum).</summary>
    public sealed class DItem
    {
        public ulong Uid { get; set; }
        public uint Cfg { get; set; }
        public uint Count { get; set; } = 1;
        public uint DurCur { get; set; }
        public uint DurMax { get; set; }

        public bool HasDur => DurMax > 0;

        public DItem Clone() => (DItem)MemberwiseClone();

        public PbW Write()
        {
            var w = new PbW().V(1, Uid).V(2, Cfg).V(3, Count);
            if (HasDur)
                w.M(11, new PbW().V(1, DurCur).V(2, DurMax).Packed(3, [0UL]));
            return w;
        }

        public static DItem? Read(PbMsg? m)
        {
            if (m is null || !m.Has(2))
                return null;
            var item = new DItem { Uid = m.U(1), Cfg = (uint)m.U(2), Count = (uint)m.U(3) };
            if (m.Msg(11) is { } dur)
            {
                item.DurCur = (uint)dur.U(1);
                item.DurMax = (uint)dur.U(2);
            }

            return item;
        }
    }

    /// <summary>What the account has done on a stage: first entered, extractions, bosses beaten.</summary>
    public sealed class DExplore
    {
        public long Since { get; set; }
        public uint Extractions { get; set; }
        public uint Bosses { get; set; }
        public uint Flags { get; set; }
    }

    /// <summary>One slot of the workbench: a recipe being crafted.</summary>
    public sealed class DCraft
    {
        public uint Recipe { get; set; }
        public uint Count { get; set; }
        public long FinishAt { get; set; }
        public Dictionary<uint, uint> Materials { get; set; } = [];
    }

    /// <summary>
    /// What the account owns in the Darkov mode (the hub): the storage grid, the backpack with its equipment and quick slots, and the progress.
    /// Persisted per account (<c>darkov.profile</c>); <see cref="DarkovProfileService"/> builds a starter kit for a new account.
    /// </summary>
    public sealed class DarkovProfile
    {
        public const int StashSlots = 50;

        public uint Hero { get; set; } = 1009;
        public uint Level { get; set; } = 1;
        public uint Exp { get; set; }
        public ulong NextUid { get; set; } = 1877919000000000000;
        public List<DItem?> Stash { get; set; } = [];
        public List<DItem?> Bag { get; set; } = [];
        public List<DItem?> Equip { get; set; } = [null, null, null, null];
        public uint[] QuickSlots { get; set; } = new uint[6];
        public Dictionary<uint, uint> Ammo { get; set; } = [];
        public List<uint> UnlockedStages { get; set; } = [1000, 1001, 1002, 2001, 8101, 9963];
        /// <summary>Goods bought today in the store (EsGoods id -> times).</summary>
        public Dictionary<uint, uint> Bought { get; set; } = [];
        /// <summary>Exploration of each stage the account has entered.</summary>
        public Dictionary<uint, DExplore> Stages { get; set; } = [];
        public List<DCraft?> Crafts { get; set; } = [null, null, null];
        public Dictionary<uint, DQuest> Quests { get; set; } = [];
        public Dictionary<string, uint> Settings { get; set; } = [];
        public uint CurrentPoi { get; set; } = 10010001;
        public uint HeroHp { get; set; } = 45000;

        public ulong NewUid() => ++NextUid;

        public void Normalize(int bagSlots = 20)
        {
            while (Stash.Count < StashSlots)
                Stash.Add(null);
            while (Bag.Count < bagSlots)
                Bag.Add(null);
            while (Equip.Count < 4)
                Equip.Add(null);
        }
    }
}
