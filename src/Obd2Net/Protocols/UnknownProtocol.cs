﻿using System;
using Obd2Net.InfrastructureContracts.Protocols;

namespace Obd2Net.Protocols
{
    public class UnknownProtocol : ProtocolBase
    {
        protected override int TxIdEngine => 0;

        public override string ElmName => "Unknown";

        public override string ElmId => "U";

        public override bool ParseFrame(IFrame frame)
        {
            throw new NotImplementedException();
        }

        public override bool ParseMessage(IMessage message)
        {
            throw new NotImplementedException();
        }
    }
}