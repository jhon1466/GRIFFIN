using Google.Protobuf;
using Griffin.Common.Proto;
using Griffin.Common.Table;
using Griffin.Database.Entities;
using Griffin.Database.Services;
using Serilog;

namespace Griffin.GameServer.Services
{
    public sealed record TalentUnlockResult(uint TalentId, ulong PointId, uint Level, IReadOnlyDictionary<uint, uint> Cost, IReadOnlyList<uint> Bits);

    /// <summary>
    /// The client keeps the unlocked points of a character as a BIT LIST (<c>Gun</c> field 51 and field 5 of <c>SC_GunTalentLvUp</c>): bit
    /// <c>SquadTalentGroupData</c> column 3 of the point, 32 per word. Without it the node never showed as active (real client 2026-10-16:
    /// the unlock was paid and answered, the panel stayed the same and the second click was refused). ASSUMPTION A-49.
    /// </summary>
    public static class TalentBits
    {
        internal static ITableService? Tables { get; set; }

        private static Dictionary<ulong, uint>? index;

        private static Dictionary<uint, uint>? treeOfGun;
        private static Dictionary<uint, List<ulong>>? pointsOfTree;
        private static Dictionary<ulong, uint>? pointType;

        private static void Build()
        {
            if (treeOfGun is not null || Tables is null)
                return;

            // SquadTalentGunData: column 8 = the tree of the character, column 13 = the public key item it gives once the tree is complete
            treeOfGun = Tables.GetTable<SquadTalentGunData>().ToDictionary(g => TableFields.U(g, 1), g => TableFields.U(g, 8));
            pointsOfTree = [];
            foreach (SquadTalentTreeData row in Tables.GetTable<SquadTalentTreeData>())
            {
                uint tree = TableFields.U(row, 2);
                if (!pointsOfTree.TryGetValue(tree, out List<ulong>? list))
                    pointsOfTree[tree] = list = [];
                list.AddRange(TableFields.U64List(row, 8));
            }

            pointType = [];
            foreach (SquadTalentGroupData point in Tables.GetTable<SquadTalentGroupData>())
                pointType[TableFields.U64(point, 1)] = TableFields.U(point, 6);
        }

        /// <summary>The tree of the character (SquadTalentGunData column 8); 0 when unknown.</summary>
        public static uint TreeOf(uint gunId)
        {
            Build();
            return treeOfGun is not null && treeOfGun.TryGetValue(gunId, out uint tree) ? tree : 0;
        }

        /// <summary>Every point of the tree except the key nodes (point type 4 = public key, 6 = Mind Spiral key) is unlocked: the client then offers the public key.</summary>
        public static bool AllUnlocked(uint gunId, IEnumerable<ulong> unlocked)
        {
            Build();
            uint tree = TreeOf(gunId);
            if (tree == 0 || pointsOfTree is null || pointType is null || !pointsOfTree.TryGetValue(tree, out List<ulong>? points))
                return false;

            HashSet<ulong> have = unlocked.ToHashSet();
            List<ulong> needed = points.Where(p => pointType.GetValueOrDefault(p) is not (4 or 6)).ToList();
            return needed.Count > 0 && needed.All(have.Contains);
        }

        /// <summary>The points of a character's tree that have the given type (SquadTalentGroupData column 6): 4 = public key node, 6 = Mind Spiral key node.</summary>
        public static IReadOnlyList<ulong> PointsOfType(uint gunId, uint type)
        {
            Build();
            uint tree = TreeOf(gunId);
            if (tree == 0 || pointsOfTree is null || pointType is null || !pointsOfTree.TryGetValue(tree, out List<ulong>? points))
                return [];

            return points.Where(p => pointType.GetValueOrDefault(p) == type).ToList();
        }

        /// <summary>
        /// The points the client must see as unlocked: the unlocked ones plus the golden public-key node (type 4) once the key was received
        /// (the node is "taken" by receiving the key and it is what leads to the Mind Spiral node of type 6). ASSUMPTION A-59.
        /// </summary>
        public static IReadOnlyCollection<ulong> EffectivePoints(Database.Entities.GunEntity gun)
        {
            IEnumerable<ulong> points = gun.TalentTree.Keys;
            return gun.IsGetPublicTalentSkillItem ? points.Concat(PointsOfType(gun.GunId, 4)).Distinct().ToList() : points.ToList();
        }

        /// <summary>The public key item (ItemData id) the character gets from its tree: SquadTalentGunData column 13.</summary>
        public static uint KeyItemOf(uint gunId)
        {
            if (Tables is null)
                return 0;

            IMessage? gun = Tables.GetTable<SquadTalentGunData>().FirstOrDefault(g => TableFields.U(g, 1) == gunId);
            return gun is null ? 0 : TableFields.U(gun, 13);
        }

        public static IReadOnlyList<uint> Words(IEnumerable<ulong> points)
        {
            if (Tables is null)
                return [];

            index ??= Tables.GetTable<SquadTalentGroupData>().ToDictionary(p => TableFields.U64(p, 1), p => TableFields.U(p, 3));
            List<uint> words = [];
            foreach (ulong point in points)
            {
                if (!index.TryGetValue(point, out uint bit))
                    continue;

                while (words.Count <= bit / 32)
                    words.Add(0);
                words[(int)(bit / 32)] |= 1u << (int)(bit % 32);
            }

            return words;
        }
    }

    /// <summary>
    /// Squad talent tree of a character (技能树), read by field number from client 1189260:
    /// <c>SquadTalentTreeData</c> (1 = group, 2 = character id, 3 = next group, 4 = open points of the group) and
    /// <c>SquadTalentGroupData</c> (1 = point id, 2 = group, 4 = points it leads to, 5 = character-level unlock condition, 10 = cost).
    /// A point is unlocked once: <c>GunEntity.TalentTree[point] = 1</c> and its cost is added to <c>GunTalentConsume</c>. A point
    /// can be unlocked when it is an open point of the first group of the tree or when an unlocked point leads to it.
    /// Assumptions: in <c>CS_GunTalentLvUp</c> the 64-bit field is the point id (the 32-bit one is echoed; if the two are swapped the
    /// other one is tried) and the answered point level is 1.
    /// </summary>
    public interface ITalentService : IGameService
    {
        /// <summary>Unlocks the point; on refusal returns null and sets <see cref="LastError"/>.</summary>
        TalentUnlockResult? Unlock(uint uid, uint talentId, ulong groupId, List<IMessage> messages);

        HNINDAOJCKP LastError { get; }

        /// <summary>
        /// Resets the whole tree of the character that <paramref name="talentId"/> designates (its id, its tree or one of its points) and
        /// refunds everything the tree consumed. ASSUMPTION (no official capture): the refund is complete and free.
        /// </summary>
        bool Reset(uint uid, uint talentId, List<IMessage> messages);
    }

    public sealed class TalentService(
        IAccountService accountService,
        IGunService gunService,
        ITableService tableService,
        IItemUseService itemUse,
        IRewardService rewardService,
        IAchievementService achievements) : ITalentService
    {
        public HNINDAOJCKP LastError { get; private set; }

        private readonly bool registered = Register(tableService);

        private static bool Register(ITableService tables)
        {
            TalentBits.Tables = tables;
            return true;
        }

        private TalentUnlockResult? Fail(HNINDAOJCKP error)
        {
            LastError = error;
            return null;
        }

        public TalentUnlockResult? Unlock(uint uid, uint talentId, ulong groupId, List<IMessage> messages)
        {
            IMessage? point = Point(groupId) is { } p ? p : Point(talentId);
            if (point is null)
                return Fail(HNINDAOJCKP.ClientInputInvalid);

            ulong pointId = TableFields.U64(point, 1);
            uint group = TableFields.U(point, 2);
            IMessage? treeRow = tableService.GetTable<SquadTalentTreeData>().FirstOrDefault(t => TableFields.U(t, 1) == group);
            // SquadTalentTreeData column 2 names the TREE TEMPLATE, not always the character (real client 2026-10-16: character 1080 sends
            // talentId=1080 with point 1063001001 of tree 1063); the character is the one the client names, and the point's own condition
            // (an achievement "character N reaches level L") must belong to it. ASSUMPTION A-47.
            uint treeKey = treeRow is null ? 0 : TableFields.U(treeRow, 2);
            GunEntity[] owned = accountService.GetByUid(uid)?.Guns.ToArray() ?? [];
            GunEntity? gun = owned.FirstOrDefault(g => g.GunId == talentId) ?? owned.FirstOrDefault(g => g.GunId == treeKey);
            if (gun is not null && TalentBits.TreeOf(gun.GunId) is var gunTree and not 0 && gunTree != treeKey)
                return Fail(HNINDAOJCKP.ClientInputInvalid);
            if (gun is null)
                return Fail(HNINDAOJCKP.GunNotFound);
            uint characterId = gun.GunId;

            uint unlockCondition = TableFields.U(point, 5);
            if (unlockCondition != 0 && !achievements.Completed(uid).ContainsKey(unlockCondition))
                return Fail(HNINDAOJCKP.UserSystemLocked);

            if (gun.TalentTree.ContainsKey(pointId))
                return Fail(HNINDAOJCKP.ClientInputInvalid);

            bool reachable = FirstGroupOpenPoints(treeKey).Contains(pointId)
                || TalentBits.EffectivePoints(gun).Any(unlocked => Point(unlocked) is { } from && TableFields.U64List(from, 4).Contains(pointId));
            if (!reachable)
                return Fail(HNINDAOJCKP.UserSystemLocked);

            Dictionary<uint, uint> cost = TableFields.Map(point, 10).ToDictionary(pair => pair.Key, pair => pair.Value);
            if (!itemUse.Spend(uid, cost.ToDictionary(pair => pair.Key, pair => (int)pair.Value), messages))
                return Fail(HNINDAOJCKP.ItemNotEnough);

            gun.TalentTree[pointId] = 1;
            foreach ((uint item, uint count) in cost)
                gun.GunTalentConsume[item] = gun.GunTalentConsume.GetValueOrDefault(item) + count;

            if (!gun.IsAllTalentUnlock && TalentBits.AllUnlocked(gun.GunId, gun.TalentTree.Keys))
            {
                // the client offers the public key only after this push (it never derives it from the points)
                gun.IsAllTalentUnlock = true;
                messages.Add(new SC_GunTalnetAllUnlock { GunId = gun.GunId });
            }

            gunService.Save();
            Log.Information("Talent: account {Uid} gun {Gun} unlocked point {Point} for {Cost}.", uid, characterId, pointId, string.Join(",", cost.Select(p => $"{p.Key}x{p.Value}")));
            return new TalentUnlockResult(talentId, groupId, 1, cost, TalentBits.Words(TalentBits.EffectivePoints(gun)));
        }

        public bool Reset(uint uid, uint talentId, List<IMessage> messages)
        {
            GunEntity[] guns = accountService.GetByUid(uid)?.Guns.ToArray() ?? [];
            GunEntity? gun = guns.FirstOrDefault(g => g.GunId == talentId);
            if (gun is null && tableService.GetTable<SquadTalentTreeData>().FirstOrDefault(r => TableFields.U(r, 1) == talentId) is { } byGroup)
                gun = guns.FirstOrDefault(g => g.GunId == TableFields.U(byGroup, 2));
            if (gun is null && Point(talentId) is { } byPoint
                && tableService.GetTable<SquadTalentTreeData>().FirstOrDefault(r => TableFields.U(r, 1) == TableFields.U(byPoint, 2)) is { } pointGroup)
                gun = guns.FirstOrDefault(g => g.GunId == TableFields.U(pointGroup, 2));
            if (gun is null)
                gun = guns.FirstOrDefault(g => g.TalentTree.ContainsKey(talentId));
            if (gun is null)
            {
                LastError = HNINDAOJCKP.GunNotFound;
                return false;
            }

            // the reserved keys are not consumption (core level, peak-key slots): they are neither refunded nor cleared
            foreach ((uint item, uint count) in gun.GunTalentConsume)
                if (count > 0 && item < TalentKeySlots.ReservedBase)
                    rewardService.Grant(uid, item, (int)count, messages);

            gun.TalentTree = [];
            gun.GunTalentConsume = gun.GunTalentConsume.Where(p => p.Key >= TalentKeySlots.ReservedBase).ToDictionary(p => p.Key, p => p.Value);
            gun.TalentResetNum++;
            gunService.Save();
            Log.Information("Talent: account {Uid} reset the tree of gun {Gun} (reset #{Count}).", uid, gun.GunId, gun.TalentResetNum);
            return true;
        }

        private IMessage? Point(ulong id) => tableService.GetTable<SquadTalentGroupData>().FirstOrDefault(p => TableFields.U64(p, 1) == id);

        /// <summary>Open points of the first group belonging to a character (the one no other group leads to).</summary>
        private HashSet<ulong> FirstGroupOpenPoints(uint characterId)
        {
            List<IMessage> groups = tableService.GetTable<SquadTalentTreeData>().Where(t => TableFields.U(t, 2) == characterId).Cast<IMessage>().ToList();
            HashSet<uint> followed = groups.SelectMany(g => TableFields.List(g, 3)).ToHashSet();
            IMessage? first = groups.FirstOrDefault(g => !followed.Contains(TableFields.U(g, 1)));
            return first is null ? [] : TableFields.U64List(first, 4).ToHashSet();
        }
    }
}
