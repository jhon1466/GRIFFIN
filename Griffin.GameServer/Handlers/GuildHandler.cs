using Google.Protobuf;
using Griffin.Common.Networking;
using Griffin.Common.Proto;
using Griffin.Common.Protocol;
using Griffin.Database.Entities;
using Griffin.GameServer.Extensions;
using Griffin.GameServer.Services;

namespace Griffin.GameServer.Handlers
{
    public sealed class GuildHandler(IProfileService profileService) : GuildHandlerBase
    {
        private const ulong LocalGuildId = 123123;
        private const string LocalGuildName = "Griffin";
        // Every account is announced as a member of the fixed guild of GuildInfo, so creating or joining another one is refused
        // with the client's own error.
        public override void HandleCreateGuild(CS_CreateGuild request, Connection connection) =>
            connection.Refuse(MsgId.MsgCsCreateGuild, HNINDAOJCKP.GuildAlreadyJoinOther);

        public override void HandleJoinGuild(CS_JoinGuild request, Connection connection) =>
            connection.Refuse(MsgId.MsgCsJoinGuild, HNINDAOJCKP.GuildAlreadyJoinOther);

        public override void HandleGuildInfo(CS_GuildInfo request, Connection connection)
        {
                     connection.Send(new SC_GuildInfo
            {
                EGACCPJBFAD = new OJHDHMDKIEK
                {
                    Id = LocalGuildId,
                    Name = LocalGuildName,
                    Level = 23,
                    NLDDNFLPMNI = "griffin",
                    Exp = 32,
                    NEDCICLLAGJ = 0,
                    CPAHOGDFLFH = 3,
                    NDIFIFKGALK = 4,
                    PBMLPPJBCHN = 123123,
                    GBJBCGBOMAC = "griffin",
                    FFFHMCINLBK = false,
                    FAIMMOELGOE = 0,
                    IDBBNMIDLAE = { 3, 4, 10 },
                    DMBCJBNCGPE = { 14, 11, 17, 18, 15 },
                    FKMGNLFONKO = 1,
                    LBCCLINKDMP = 0,
                },
                QuestId = 10004,
                MDDICHKNEPE = false,
                SeasonId = 10032,
                ONBCNAAHJMA = 60,
            });
        }

        public override void HandleGetGuildBattleMissionInfo(CS_GetGuildBattleMissionInfo request, Connection connection)
        {
            // Guild-war missions need a real guild roster/season; this private server
            // has neither. A typed empty state keeps the login/social panel responsive.
            connection.Send(new SC_GetGuildBattleMissionInfo());
        }
        public override void HandleGuildMemberList(CS_GuildMemberList request, Connection connection)
        {
            if (connection.Account is null)
            {
                connection.Send(29, new SC_GuildMemberList());
                return;
            }

            AccountEntity account = connection.Account;
            ProfileState profile = profileService.Get(account.Uid);
            User member = new()
            {
                Uid = account.Uid,
                Name = string.IsNullOrWhiteSpace(profile.Name) ? "griffin" : profile.Name,
                Level = 60,
                Sex = Sex.Female,
                GuildId = LocalGuildId,
                GuildName = LocalGuildName,
                GunNum = (uint)account.Guns.Count,
                Portrait = profile.Portrait ?? 21089,
                PortraitFrame = profile.PortraitFrame ?? 24164,
                Birthday = profile.Birthday ?? 101,
                Motto = profile.Motto ?? string.Empty,
            };
            profileService.ApplyTo(account.Uid, member);

            connection.Send(29, new SC_GuildMemberList
            {
                LLEPAHIHEBE =
                {
                    new IOFPLLMGFNF
                    {
                        PBJAABKNKAP = new BinaryUser { Data = member.ToByteString() },
                        DAGBCMOABEA = BLPACHOMKPJ.JobLeader,
                        Uid = account.Uid,
                    },
                },
                PEFCJOKNJCN = LocalGuildId,
            });
        }
        public override void HandleGetGuildSupplyList(CS_GetGuildSupplyList request, Connection connection)
        {
            connection.Send(new SC_GetGuildSupplyList());
        }

        public override void HandleGuildApplyList(CS_GuildApplyList request, Connection connection)
        {
            connection.Send(new SC_GuildApplyList());
        }

        public override void HandleGetGuildScoreRank(CS_GetGuildScoreRank request, Connection connection)
        {
          
        }
    }
}
