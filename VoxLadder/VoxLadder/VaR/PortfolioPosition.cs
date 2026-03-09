using QX.FinLib.Common;
using System;

namespace QX.BackTesting.Strategies.QuantumVaR
{
    public class PortfolioPosition
    {
        public double Strike { get; set; }
        public OptionType Type { get; set; } // CE or PE
        public DateTime Expiry { get; set; }
        public int NetLots { get; set; } // signed: +ve = long, -ve = short
        public double AvgEntryPrice { get; set; } // premium at entry

        public override string ToString()
        {
            string side = NetLots > 0 ? "Long" : "Short";
            return $"{side} {Math.Abs(NetLots)}L {Strike}{Type} Exp:{Expiry:dd-MMM-yy} @{AvgEntryPrice:F2}";
        }
    }

    public struct OpenLeg
    {
        public double Strike;
        public OptionType Type; // CE or PE
        public DateTime Expiry;
        public int Direction; // +1 for long, -1 for short
        public double EntryPrice;
        public string InstrumentName;

        public override string ToString()
        {
            string side = Direction > 0 ? "Long" : "Short";
            return $"{side} {Strike}{Type} @{EntryPrice:F2}";
        }
    }
}
