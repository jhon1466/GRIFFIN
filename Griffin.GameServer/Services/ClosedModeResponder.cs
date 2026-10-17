using System.Text.RegularExpressions;
using Griffin.Common.Networking;
using Griffin.Common.Packets;
using Griffin.Common.Proto;
using Griffin.Common.Protocol;
using Griffin.GameServer.Extensions;
using Serilog;

namespace Griffin.GameServer.Services
{
    /// <summary>
    /// Actions of game modes and events this server does not run (seasons that are not open, activities that are not in the calendar) are answered with the
    /// client's own "this mode is not open / has ended" error of that family instead of the generic "function locked" (<c>-10018</c>) that the dispatcher sends
    /// to anything it does not know. The answer is true to the state the server announces (the calendar and the mode queries report nothing open) and is
    /// the message the official client shows when the player acts on a closed mode. Plain queries are not touched here (the dispatcher answers them empty);
    /// requests with a handler or another responder never get here. ASSUMPTION A-23: the first matching rule decides the error.
    /// </summary>
    public sealed class ClosedModeResponder : IFallbackResponder
    {
        private static readonly (Regex Name, HNINDAOJCKP Error)[] Rules =
        [
            (new Regex("^(GuildWar|BuyGuildBattleTicket|GainSupply)", RegexOptions.Compiled), HNINDAOJCKP.GuildNotFound),
            (new Regex("^Moment(Bless|BlessAll)$", RegexOptions.Compiled), HNINDAOJCKP.BlessOverLimit),
            (new Regex("^(ChatChannel|AddRobotChat)$", RegexOptions.Compiled), HNINDAOJCKP.ClientInputInvalid),
            (new Regex("^StoreTagRefresh", RegexOptions.Compiled), HNINDAOJCKP.StoreManualRefreshForbidden),
            (new Regex("^StoreGetRechargeReward", RegexOptions.Compiled), HNINDAOJCKP.StoreRechargeRewardNotReach),
            (new Regex("^StoreCartGoodSelected", RegexOptions.Compiled), HNINDAOJCKP.StoreCartGoodNotExist),
            (new Regex("^GunWeaponEvolution", RegexOptions.Compiled), HNINDAOJCKP.GunWeaponEvolutionValid),
            (new Regex("^GunWeaponModCalibration", RegexOptions.Compiled), HNINDAOJCKP.CalibrationFunctionClosed),
            (new Regex("^GetCareRewards", RegexOptions.Compiled), HNINDAOJCKP.ActivityNotOpenOrClosed),
            (new Regex("^GachaGetPhaseReward", RegexOptions.Compiled), HNINDAOJCKP.GachaPhaseRewardCountNotReach),
            (new Regex("^GetBattlepassRewardTimeLimit", RegexOptions.Compiled), HNINDAOJCKP.BattlepassRewardLock),
            (new Regex("^(DarkZone)", RegexOptions.Compiled), HNINDAOJCKP.DarkZoneNotStart),
            (new Regex("^SimCombatMythic", RegexOptions.Compiled), HNINDAOJCKP.SimCombatMythicNotStarted),
            (new Regex("^SimCombatUnite", RegexOptions.Compiled), HNINDAOJCKP.SimCombatUniteMissionIdNotOpen),
            (new Regex("^SimCombat", RegexOptions.Compiled), HNINDAOJCKP.SimCombatWeeklyNotOpened),
            (new Regex("^NrtPvp|^PvpCycle|^Pvp", RegexOptions.Compiled), HNINDAOJCKP.NrtPvpHasClosed),
            (new Regex("^HighPvp", RegexOptions.Compiled), HNINDAOJCKP.HighPvpIsClosed),
            (new Regex("^AsyncPvp|^AsyncDefend", RegexOptions.Compiled), HNINDAOJCKP.AsyncPvpIsClosed),
            (new Regex("^Theme", RegexOptions.Compiled), HNINDAOJCKP.ThemeActivityNotStartOrClosed),
            (new Regex("^ActivityCheckIn|^ActivityDelaySignIn|^ActivityPaidSignin", RegexOptions.Compiled), HNINDAOJCKP.CheckinActivityNotOpen),
            (new Regex("^EventSignin", RegexOptions.Compiled), HNINDAOJCKP.EventSigninBpClosed),
            (new Regex("^Shootspace", RegexOptions.Compiled), HNINDAOJCKP.ShootspaceNotOpen),
            (new Regex("^TpsShoot", RegexOptions.Compiled), HNINDAOJCKP.TpsShootNotOpen),
            (new Regex("^MiniGame", RegexOptions.Compiled), HNINDAOJCKP.MiniGameNotOpen),
            (new Regex("^LobbyGame", RegexOptions.Compiled), HNINDAOJCKP.MiniGameNotOpen),
            (new Regex("^PazzleStage", RegexOptions.Compiled), HNINDAOJCKP.PazzleStageNotOpen),
            (new Regex("^Jigsaw", RegexOptions.Compiled), HNINDAOJCKP.JigsawNotOpen),
            (new Regex("^OperationMiniGame", RegexOptions.Compiled), HNINDAOJCKP.OperationActivityNotOpen),
            (new Regex("^Collection", RegexOptions.Compiled), HNINDAOJCKP.CollectionNotStart),
            (new Regex("^(Geopolitic|SandBox|RichMan|Monopoly|Rouge|SimCafe|KittyCafe|Bingo|OpenBingoCard|TakeBingo|OpenGrid|InteractWithGrid|FreeSkin|EventEndlessTower|Activity|TakeActivity|ActicityReset|TakeWarmUp|UnlockAdvancedReward|GainIndividualScore|GainGuildScore|RubyShop|EnterDungeon|GetDispatchReward)", RegexOptions.Compiled), HNINDAOJCKP.ActivityNotOpenOrClosed),
            (new Regex("^(ActiveInspiration|AddInspirationOrder|ExchangeInspiration|RefreshInspirationOrder|ConfirmExchange)", RegexOptions.Compiled), HNINDAOJCKP.InspirationOrderNotFound),
        ];

        public bool TryRespond(Connection connection, BasePacket packet)
        {
            string name = packet.MsgId.ToString();
            if (!name.StartsWith("MsgCs", StringComparison.Ordinal) || name.Length <= 5)
                return false;

            string suffix = name[5..];
            if (!PacketDispatcher.IsActionRequest(suffix) || !Enum.TryParse("MsgSc" + suffix, out MsgId reply) || reply == MsgId.MsgNone)
                return false;

            foreach ((Regex pattern, HNINDAOJCKP error) in Rules)
            {
                if (!pattern.IsMatch(suffix))
                    continue;

                connection.Refuse(packet.MsgId, error);
                Log.Information("CLOSED-MODE {Request} refused with {Error}: the mode is not open on this server.", name, error);
                return true;
            }

            return false;
        }
    }
}
