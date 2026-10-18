using Griffin.Common.Networking;
using Griffin.Common.Protocol;
using Griffin.GameServer.Extensions;
using Griffin.Common.Proto;
using Griffin.Common.Table;
using Griffin.GameServer.Services;

namespace Griffin.GameServer.Handlers
{
    /// <summary>
    /// Public talent skill items. The three items of the account are the ones of the original capture (ASSUMPTION: the server has no
    /// source that grants more); which character wears each one is NOT stored here but read from the characters
    /// (<c>GunEntity.PublicTalentSkillItemsUid</c>), so it survives a relaunch and always agrees with <c>SC_Guns</c>.
    /// </summary>
    public sealed class PublicTalentHandler(IGunService gunService, IAccountStateService accountState, Griffin.Common.Table.ITableService tableService, IItemUseService itemUse) : PublicTalentHandlerBase
    {
        public static readonly (ulong Id, uint StcId)[] Items =
        [
            (1843288605358841857UL, 91025),
            (1843288605358841858UL, 93007),
            (1843288605358841856UL, 91021),
        ];

        /// <summary>A public key the account got by completing the talent tree of a character (the three base items never change).</summary>
        public sealed class ExtraItem
        {
            public ulong Id { get; set; }

            public uint StcId { get; set; }
        }

        public const string ExtraKey = "publictalent.extra";

        /// <summary>State key of the affixes of each key (uid as text -> four 16-bit pairs: high byte = DevelopProperty, low byte = level), A-75.</summary>
        public const string AffixKey = "publictalent.affixes";

        public static ulong AffixesOf(IAccountStateService state, uint uid, ulong keyId) =>
            (state.Get<Dictionary<string, ulong>>(uid, AffixKey) ?? []).GetValueOrDefault(keyId.ToString());

        public static IReadOnlyList<(ulong Id, uint StcId)> ItemsOf(IAccountStateService state, uint uid) =>
            [.. Items, .. (state.Get<List<ExtraItem>>(uid, ExtraKey) ?? []).Select(e => (e.Id, e.StcId))];

        // The names of this message come from an older build: the 4.0.5136 client (NEHFNMNLEHL) reads field 4 as the AFFIXES of the key (four 16-bit pairs)
        // and field 5 as its lock (A-75).
        public static PublicSkillItem ToProto(ulong id, uint stcId, uint gunId, ulong affixes = 0) =>
            new() { Id = id, StcId = stcId, GunId = gunId, Locked = affixes, MDFIDAFPBJN = false };

        public override void HandlePublicTalentSkillItems(CS_PublicTalentSkillItems request, Connection connection)
        {
            Database.Entities.GunEntity[] guns = connection.Account is null ? [] : gunService.GetPlayerGuns(connection.Account.Uid);
            SC_PublicTalentSkillItems reply = new();
            foreach ((ulong id, uint stc) in connection.Account is null ? Items : ItemsOf(accountState, connection.Account.Uid))
                reply.PublicSkillItems.Add(ToProto(id, stc, guns.FirstOrDefault(g => g.PublicTalentSkillItemsUid.Contains(id))?.GunId ?? 0,
                    connection.Account is null ? 0 : AffixesOf(accountState, connection.Account.Uid, id)));
            connection.Send(reply);
        }

        /// <summary>
        /// The client chooses, resets or raises the affixes of a key by sending the complete new configuration in field 6 (up to four pairs, high byte =
        /// property, low byte = level) and expects the confirmed one back (<c>KADHBNLJHPP</c>). It is checked against the tables (properties of
        /// <c>EntryData</c> column 4 for the rank of the key, levels 1 to <c>EntryData</c> column 3), saved and returned; the cost of the operation
        /// (<c>EntryPropertLevelData</c> columns 5 and 6) is not charged because the table does not say which of the two applies to each type (A-75).
        /// </summary>
        public override void HandlePublicTalentItemLevelUp(CS_PublicTalentItemLevelUp request, Connection connection)
        {
            if (connection.Account is null)
                return;

            uint uid = connection.Account.Uid;
            ulong bits = request.Locked;
            if (ItemsOf(accountState, uid).All(item => item.Id != request.Id) || !ValidAffixes(bits, ItemsOf(accountState, uid).First(item => item.Id == request.Id).StcId))
            {
                connection.Refuse(MsgId.MsgCsPublicTalentItemLevelUp, HNINDAOJCKP.ClientInputInvalid);
                return;
            }

            List<Google.Protobuf.IMessage> messages = [];
            lock (AccountLock.For(uid))
            {
                Dictionary<string, ulong> saved = accountState.Get<Dictionary<string, ulong>>(uid, AffixKey) ?? [];
                uint stc = ItemsOf(accountState, uid).First(item => item.Id == request.Id).StcId;
                if (request.Type != PEJMKCFPIDM.TypeReset)
                {
                    TalentKeyData? keyRow = tableService.GetTable<TalentKeyData>().FirstOrDefault(row => TableFields.U(row, 1) == stc);
                    IReadOnlyDictionary<int, int> keyBase = keyRow is null ? new Dictionary<int, int>() : BattleUnitBuilder.RowByDevelopProperty(tableService, TableFields.U(keyRow, 5));

                    Dictionary<uint, int> cost = KeyAffixes.Cost(tableService, saved.GetValueOrDefault(request.Id.ToString()), bits, stc, keyBase);
                    if (cost.Count > 0 && !itemUse.Spend(uid, cost, messages))
                    {
                        connection.Refuse(MsgId.MsgCsPublicTalentItemLevelUp, HNINDAOJCKP.ItemNotEnough);
                        return;
                    }
                }

                saved[request.Id.ToString()] = bits;
                accountState.Set(uid, AffixKey, saved);
            }

            messages.Add(new SC_PublicTalentItemLevelUp
            {
                Id = request.Id, Type = request.Type, Index = request.Index, Level = request.Level, NLHNEDAJMAD = request.NLHNEDAJMAD, Locked = bits,
            });
            connection.SendAutoEncrypted(messages.ToArray());
        }

        private bool ValidAffixes(ulong bits, uint keyStcId)
        {
            if (bits == 0)
                return true;

            return KeyAffixes.Valid(tableService, bits, keyStcId);
        }
    }
}
