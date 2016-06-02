﻿using System;
using Obd2Net.InfrastructureContracts.Enums;
using Obd2Net.InfrastructureContracts.Protocols;

namespace Obd2Net.InfrastructureContracts
{
    public interface IPort
    {
        IObdConfiguration Config { get; }
        OBDStatus Status { get; }
        ECU[] Ecus { get; }
        IProtocol Protocol { get; }
        void Close();
        IMessage[] SendAndParse(string cmd);
        string[] Send(string cmd, TimeSpan? delay = null);
        bool Connect();
    }
}