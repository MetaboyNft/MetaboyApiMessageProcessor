using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MetaboyApiMessageProcessor.Models
{
    internal class CompletedClaim
    {
        public uint Id { get; set; } = 0;
        public string Address { get; set; } = "";
        public string NftData { get; set; } = "";
        public string ClaimedDate { get; set; } = "";
        public string Amount { get; set; } = "";
    }
}

