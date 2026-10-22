using Griffin.Common.Networking;
using Griffin.Common.Proto;
using Griffin.GameServer.Extensions;
using Griffin.Common.Protocol;

namespace Griffin.GameServer.Handlers
{
    public sealed class DarkZoneHandler(Griffin.GameServer.Services.IModProgressService modProgress) : DarkZoneHandlerBase
    {
        public override void HandleDarkZoneStorage(CS_DarkZoneStorage request, Connection connection)
        {
                    connection.Send(new SC_DarkZoneStorage
                    {
                        StorageBuyTimes = 0,
                        Storage = new PileBag
                        {
                            Entities = { },
                            Others = { },
                            House = true,
                            IHOAILNKNNE = { },
                            Weapons = { },
                            WeaponMods = { },
                        },
                        Bag = new PileBag
                        {
                            Entities = { },
                            Others = { },
                            House = false,
                            IHOAILNKNNE = { },
                            Weapons = { },
                            WeaponMods = { },
                        },
                        Equips = { },
                    });
        }

        public override void HandleDarkZoneQuestModeInfo(CS_DarkZoneQuestModeInfo request, Connection connection)
        {
                    connection.Send(new SC_DarkZoneQuestModeInfo
                    {
                        JIAMDNEMBAK = { },
                    });
        }

        /// <summary>
        /// The "Dismantle" button of the storeroom (UIRepositoryDecomposingDialog) of the 4.0.5136 client does not send CS_GunWeaponModDismantle: it sends this request
        /// with the ids of the weapon mods (log of 2026-10-22: the server refused it with DarkZoneNotStart and nothing happened). The mods are dismantled like in
        /// <see cref="GunWeaponHandler"/>; the client removes them with SC_GunWeaponModDismantle and takes what it got from the answer (A-153).
        /// </summary>
        public override void HandleDarkZoneWishDismantle(CS_DarkZoneWishDismantle request, Connection connection)
        {
            if (connection.Account is null)
                return;

            List<Google.Protobuf.IMessage> messages = [];
            Dictionary<uint, int> gains = [];
            IReadOnlyList<uint> removed = modProgress.Dismantle(connection.Account.Uid, request.Ids, messages, gains);
            if (removed.Count == 0)
            {
                connection.Refuse(MsgId.MsgCsDarkZoneWishDismantle, HNINDAOJCKP.GunWeaponModIsNotFree);
                return;
            }

            SC_GunWeaponModDismantle gone = new();
            gone.Ids.AddRange(removed.Select(i => (ulong)i));
            SC_DarkZoneWishDismantle answer = new();
            answer.Ids.AddRange(removed);
            foreach ((uint item, int count) in gains)
                answer.DropItems[item] = (uint)count;
            List<Google.Protobuf.IMessage> all = [gone, .. GunWeaponHandler.CoalesceTotals(messages), answer];
            connection.SendAutoEncrypted(all.ToArray());
        }

        public override void HandleDarkZoneWishExp(CS_DarkZoneWishExp request, Connection connection)
        {
                    connection.Send(new SC_DarkZoneWishExp
                    {
                    });
        }

        public override void HandleDarkZoneGetWeeklyInfo(CS_DarkZoneGetWeeklyInfo request, Connection connection)
        {
            switch (request.IPGJJLJMEBO)
            {
                case CS_DarkZoneGetWeeklyInfo.Types.EPIBDODMBLN.All:
                    connection.Send(new SC_DarkZoneGetWeeklyInfo
                    {
                        IPGJJLJMEBO = CS_DarkZoneGetWeeklyInfo.Types.EPIBDODMBLN.All,
                        BCCILNBHDAO = new HBMBNIHMKLI
                        {
                            ONMNEMDCEIJ = 1001,
                            TalentId = { },
                            IFNEDNHKBJN = HBMBNIHMKLI.Types.FHOMGAKNIAF.Easy,
                            Quests =
                            {
                                new DJIPBLHIDPA { QuestId = 3018, JONLCAGBGJE = false },
                                new DJIPBLHIDPA { QuestId = 3006, JONLCAGBGJE = false },
                                new DJIPBLHIDPA { QuestId = 3002, JONLCAGBGJE = false },
                                new DJIPBLHIDPA { QuestId = 3013, JONLCAGBGJE = false },
                            },
                            FEMJMHOADOI = 1779500060L,
                            LELDCCEAHIO = { },
                            GCACAKMMKKL = { },
                            FHIIMKLFEEH = { },
                        },
                    });
                    break;
            }
        }

        public override void HandleDarkZoneGetEthnicInfo(CS_DarkZoneGetEthnicInfo request, Connection connection)
        {
            connection.Send(new SC_DarkZoneGetEthnicInfo
            {
                CAAMMBOPHLI = 0,
                ADDDENOAOJO = 0,
                FNALHFDBDHB = 0,
                FIIIHMMPLNH = 0,
                Point = 0,
                MGFKLGLDBPE = { },
                PlanId = 170063,
                FELLJFBGNJG = { },
                JIAMIEJEDJJ = 0,
            });
        }

        public override void HandleDarkZoneGetUnlockEggStage(CS_DarkZoneGetUnlockEggStage request, Connection connection)
        {
            connection.Send(new SC_DarkZoneGetUnlockEggStage
            {
                NDINCEOKEOA = { },
            });
        }

    }
}
