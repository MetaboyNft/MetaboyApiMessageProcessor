﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LoopDropSharp
{
    public class Settings
    {
        public string LoopringApiKey { get; set; }
        public string LoopringPrivateKey { get; set; }
        public string LoopringAddress { get; set; }
        public int LoopringAccountId { get; set; }
        public long ValidUntil { get; set; }
        public int MaxFeeTokenId { get; set; }
        public string Exchange { get; set; }

        public string MMorGMEPrivateKey { get; set; }

        public string AzureServiceBusConnectionString { get; set; }

        public string AzureSqlConnectionString { get; set; }
        public string TransferMemo { get; set; }

        public string Description { get; set; }
        public string Environment { get; set; }
        public bool UseAzureKeyVault { get; set; }
    }
}
