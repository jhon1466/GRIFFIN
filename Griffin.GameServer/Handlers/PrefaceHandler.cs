using Griffin.Common.Networking;
using Griffin.Common.Proto;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Griffin.GameServer.Handlers
{
    public sealed class PrefaceRecordHandler : PrefaceRecordHandlerBase
    {
        public override void HandleGetPrefaceRecord(CS_GetPrefaceRecord request, Connection connection)
        {
            connection.Send(new SC_GetPrefaceRecord
            {
                Preface = true
            });
        }
    }
}
