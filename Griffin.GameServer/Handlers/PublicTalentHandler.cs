using Griffin.Common.Networking;
using Griffin.Common.Proto;
using Griffin.GameServer.Services;

namespace Griffin.GameServer.Handlers
{
    /// <summary>
    /// Public talent skill items. The three items of the account are the ones of the original capture (ASSUMPTION: the server has no
    /// source that grants more); which character wears each one is NOT stored here but read from the characters
    /// (<c>GunEntity.PublicTalentSkillItemsUid</c>), so it survives a relaunch and always agrees with <c>SC_Guns</c>.
    /// </summary>
    public sealed class PublicTalentHandler(IGunService gunService, IAccountStateService accountState) : PublicTalentHandlerBase
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

        public static IReadOnlyList<(ulong Id, uint StcId)> ItemsOf(IAccountStateService state, uint uid) =>
            [.. Items, .. (state.Get<List<ExtraItem>>(uid, ExtraKey) ?? []).Select(e => (e.Id, e.StcId))];

        public static PublicSkillItem ToProto(ulong id, uint stcId, uint gunId) =>
            new() { Id = id, StcId = stcId, GunId = gunId, Locked = 0, MDFIDAFPBJN = false };

        public override void HandlePublicTalentSkillItems(CS_PublicTalentSkillItems request, Connection connection)
        {
            Database.Entities.GunEntity[] guns = connection.Account is null ? [] : gunService.GetPlayerGuns(connection.Account.Uid);
            SC_PublicTalentSkillItems reply = new();
            foreach ((ulong id, uint stc) in connection.Account is null ? Items : ItemsOf(accountState, connection.Account.Uid))
                reply.PublicSkillItems.Add(ToProto(id, stc, guns.FirstOrDefault(g => g.PublicTalentSkillItemsUid.Contains(id))?.GunId ?? 0));
            connection.Send(reply);
        }

        // Public talent item levels are not stored (the item list has no level field): the request is confirmed as sent.
        public override void HandlePublicTalentItemLevelUp(CS_PublicTalentItemLevelUp request, Connection connection) =>
            connection.Send(new SC_PublicTalentItemLevelUp
            {
                Id = request.Id, Type = request.Type, Index = request.Index, Level = request.Level, NLHNEDAJMAD = request.NLHNEDAJMAD, Locked = request.Locked,
            });
    }
}
