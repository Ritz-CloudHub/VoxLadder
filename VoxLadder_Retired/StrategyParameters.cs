using System;

namespace QX.BackTesting.Strategies
{
    public partial class VoxLadder
    {
        #region Indicator Periods

        const int EMAFastPeriod = 3;
        const int EMASlowPeriod = 10;
        const int RSIPeriod = 7;

        const int VixEMAFastPeriod = 5;
        const int VixEMASlowPeriod = 20;

        const int VolSMAPeriod = 20;

        const int ObvEMAFastPeriod = 10;
        const int ObvEMASlowPeriod = 20;

        const int AdxCustomPeriod = 14;
        const int AdxDailyPeriod = 7;

        #endregion


        #region Signal Thresholds

        const double RSIBullThreshold = 45.0;
        const double RSIBearThreshold = 35.0;

        const double EMABullThreshold = 0.0;
        const double EMABearThreshold = -0.1;

        const double VolumeBullMult = 1.20;
        const double VolumeBearMult = 1.50;

        const double BullOverPowerThreshold = 0.4;
        const double BearOverPowerThreshold = -0.4;

        const int UpSwingPrimaryThreshold = 2;
        const int UpSwingConfirmThreshold = 1;
        const int DownSwingPrimaryThreshold = -2;
        const int DownSwingConfirmThreshold = -1;

        const int BearDivergenceMinCount = 4;

        #endregion


        #region Entry Filters (Time, VIX, ADX tiers)

        private readonly int BarTimePeriod = 30;
        private readonly int MinDaysToExpiry = 4;

        private readonly TimeSpan TradeStartTime = new TimeSpan(09, 17, 00);
        private readonly TimeSpan TradeEndTime = new TimeSpan(15, 25, 00);

        private readonly bool SwitchWorkingExpiry = true;
        private readonly bool AtleastOneRollOverTrade = false;
        private readonly bool AtleastOneCurrentWeekTrade = false;

        const double EMAGapMinFilter = 0.05;

        const double SdvAdxCustom = 8.0;
        const double SdvAdxDaily = 12.0;
        const double ScaleAdxDaily = 0.67;

        const double AdxCombTier1 = 3.70;
        const double AdxCombTier2 = 4.60;
        const double AdxCombTier3 = 5.80;

        const int VixTier1 = 25;
        const int VixTier2 = 19;
        const int VixTier3 = 16;

        #endregion


        #region Trade Setup (MaxLoss, POP, strike selection)

        const int SpreadLotsPerTrade = 1;
        double ExpMul = 1;

        const double TargetSellPremium = 15;
        const double BuyPremiumRatio = 0.50;

        const int MinSpreadWidth = 100;
        const int SpreadWidthBuyAdjBuffer = 50;

        const double TargetFarPremium = 15.0;
        const double FarPremiumFloorVsSell = 0.25;
        const double FarPremiumFloorVsTarget = 0.66;
        const double FarPremiumAdjMultTarget = 1.5;
        const double FarPremiumAdjMultSell = 0.35;

        const double ExpiryDayMarginMult = 1.3;
        const double ExpiryMarginScale = 1.30;

        const double TargetMaxLossPct = 0.25;
        const double TargetPOP = 85;

        #endregion


        #region Exit & Stop Loss

        const int CreditUpperBound = 2000;
        const double StopLossFactor = 1.25;
        const double StopLossFactorPartialProfit = 0.00;
        const double StopLossFactorPartialLossBase = 1.25;
        const double SwingExitPct = 1.50;

        #endregion


        #region Partial Booking / Far Leg

        const int FarLegProfitBookAdd = 25;
        const double FarLegProfitBookMult = 2.50;
        const int FarLegStopLossSub = 15;
        const double FarLegStopLossMult = 0.50;
        const double FarLegSpotMoveThreshold = 1.00;

        #endregion
    }
}
