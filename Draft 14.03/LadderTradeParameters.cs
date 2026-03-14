using System;

namespace QX.BackTesting.Strategies
{
    public partial class VoxLadder
    {
        private readonly bool EnableLadderTrade = true;
        private readonly double TargetLadderTradePremium = 15.0;
        private readonly double LadderTradePBAdd = 25.0;
        private readonly double LadderTradePBMult = 3.0;
        private readonly double LadderTradeSLPct = 20;
        private readonly double LadderTradeSpotThreshold = 1.50;
    }
}
