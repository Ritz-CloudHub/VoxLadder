namespace QX.BackTesting.Strategies
{
    public partial class VoxLadder
    {
        #region GammaBurst Parameters

        private readonly bool EnableGammaBurst = true;
        private readonly bool EnableLadder = true;
        private readonly double GammaBurstMaxDTE = 2.0;
        private readonly double GammaBurstMinDTE = 0.0;
        private readonly double GammaBurstBearMinVIX = 13;
        private readonly double GammaBurstDT0Threshold = 0.5;

        // DT0 — best
        private readonly double TargetGammaBurstPremiumDT0 = 22;
        private readonly double GammaBurstProfitBookMultipleDT0 = 2.5;
        private readonly double GammaBurstSLPctDT0 = 20;

        // DT1 — best
        private readonly double TargetGammaBurstPremiumDT1 = 25;
        private readonly double GammaBurstProfitBookMultipleDT1 = 3.0;
        private readonly double GammaBurstSLPctDT1 = 40;

        #endregion
    }
}
