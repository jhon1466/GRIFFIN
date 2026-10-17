using Google.Protobuf;
using Griffin.Common.Networking;
using Griffin.Common.Proto;
using Griffin.Common.Protocol;
using Griffin.Common.Table;
using Griffin.GameServer.Extensions;
using Griffin.GameServer.Services;

namespace Griffin.GameServer.Handlers
{
    /// <summary>
    /// Faction intelligence files (<c>CampFileData</c>: 1 = file, 2 = faction, 7 = file that must be unlocked first, 8 = reward). The state is a
    /// dictionary file -> reward taken in <c>camp.files</c>; <c>SC_CampFiles</c> lists the unlocked ones. ASSUMPTION A-24: unlocking only needs the previous
    /// file (the unlock condition ids of col 3 are client-side requirements that are not checked), the reward is paid once.
    /// </summary>
    public sealed class CampFileHandler(ITableService tableService, IAccountStateService accountState, IRewardService rewardService) : CampFileHandlerBase
    {
        private const string StateKey = "camp.files";

        private IMessage? Row(uint file) => tableService.GetTable<CampFileData>().FirstOrDefault(r => TableFields.U(r, 1) == file);

        public override void HandleCampFiles(CS_CampFiles request, Connection connection)
        {
            SC_CampFiles reply = new();
            if (connection.Account is not null)
                foreach ((uint file, bool taken) in accountState.Get<Dictionary<uint, bool>>(connection.Account.Uid, StateKey) ?? [])
                    reply.CampFiles[file] = taken;
            connection.Send(reply);
        }

        public override void HandleUnlockCampFile(CS_UnlockCampFile request, Connection connection)
        {
            if (connection.Account is null)
                return;

            lock (AccountLock.For(connection.Account.Uid))
            {
                uint uid = connection.Account.Uid;
                IMessage? row = Row(request.FileId);
                if (row is null)
                {
                    connection.Refuse(MsgId.MsgCsUnlockCampFile, HNINDAOJCKP.CampFileNotFound);
                    return;
                }

                Dictionary<uint, bool> files = accountState.Get<Dictionary<uint, bool>>(uid, StateKey) ?? [];
                uint previous = TableFields.U(row, 7);
                if (previous != 0 && !files.ContainsKey(previous))
                {
                    connection.Refuse(MsgId.MsgCsUnlockCampFile, HNINDAOJCKP.CampFileNotFound);
                    return;
                }

                files.TryAdd(request.FileId, false);
                accountState.Set(uid, StateKey, files);
                connection.Send(new SC_UnlockCampFile { FileId = request.FileId });
            }
        }

        public override void HandleCampFileTakeRewards(CS_CampFileTakeRewards request, Connection connection)
        {
            if (connection.Account is null)
                return;

            lock (AccountLock.For(connection.Account.Uid))
            {
                uint uid = connection.Account.Uid;
                IMessage? row = Row(request.FileId);
                Dictionary<uint, bool> files = accountState.Get<Dictionary<uint, bool>>(uid, StateKey) ?? [];
                if (row is null || !files.TryGetValue(request.FileId, out bool taken) || taken)
                {
                    connection.Refuse(MsgId.MsgCsCampFileTakeRewards, HNINDAOJCKP.CampFileNotFound);
                    return;
                }

                List<IMessage> messages = [];
                foreach ((uint item, uint count) in TableFields.Map(row, 8))
                    rewardService.Grant(uid, item, (int)count, messages);
                files[request.FileId] = true;
                accountState.Set(uid, StateKey, files);
                messages.Add(new SC_CampFileTakeRewards { FileId = request.FileId });
                connection.SendAutoEncrypted(messages.ToArray());
            }
        }
    }
}
