using Griffin.Common.Networking;
using Griffin.Common.Proto;

namespace Griffin.GameServer.Handlers
{
    public sealed class NewbeeQuestHandler : NewbeeQuestHandlerBase
    {
        public override void HandleGetNewbeeQuestV2(CS_GetNewbeeQuestV2 request, Connection connection)
        {
                    connection.Send(new SC_GetNewbeeQuestV2
                    {
                        ONMNEMDCEIJ = 69998,
                        LKAHOENFDFN = new KBJGHCKJJAJ
                        {
                            JODLPGFGDDN = 1,
                            BPJMFIBFGKC = 1728668611,
                            CDFBDNJIEOP = { },
                            IIOPIOOLBOA = false,
                        },
                    });
        }

    }
}
