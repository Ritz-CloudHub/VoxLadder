using Accord.Collections;
using Accord.Math.Geometry;
using Accord.Statistics.Models.Regression.Linear;
using MathNet.Numerics.Distributions;
using QX.BackTesting.Indicators;
using QX.FinLib.Data;
using QX.OptionsBTTestApp.RD;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace QX.BackTesting.Strategies
{
    public partial class VoxLadder
    {
        
        private double Signal;
        private double PrevSignal;

        
        private int? LastTradedSignal;      
       
       
        private int RunningSignal = 0;       
        private int RunningSignalCount = 0;
        private double ReferenceSpot = 0;
        private double MostFavSpot = 0; // Use full for Trend Analysis

        private DateTime ReferenceTime = DateTime.MinValue;
       
        // Moved to StrategyParameters.cs: RSIBullThreshold, RSIBearThreshold, EMABullThreshold, EMABearThreshold, VolumeBullMult, VolumeBearMult

        private string IndicatorValues;

        private void UpdateSignalState(IBarData barData)
        {

           

            if (barData == null || CustomBarCount <= 1 || OBV == null)
                return;

            if (dbg)
            {
                int x = 0;
            }

            double currentEMAFast = EMAFast[0];
            double previousEMAFast = EMAFast[1];
            double currentEMASlow = EMASlow[0];
            double currentRSI = RSI[0];
            double latestVolume = CustomBarFutVolumes.Last();
            double vixEMAFast = VixEMAFast[0];
            double vixEMASlow = VixEMASlow[0];

            double emaGap = 100 * (currentEMAFast - currentEMASlow) / currentEMASlow;
            // double emaJump = 100 * (currentEMAFast - previousEMAFast) / previousEMAFast;


            bool emaBull = !double.IsNaN(currentEMAFast) && currentEMAFast > currentEMASlow && emaGap > EMABullThreshold;
            bool emaBear = !double.IsNaN(currentEMAFast) && currentEMAFast < currentEMASlow && emaGap < EMABearThreshold;

            bool rsiBull = !double.IsNaN(currentRSI) && currentRSI > RSIBullThreshold;
            bool rsiBear = !double.IsNaN(currentRSI) && currentRSI < RSIBearThreshold;

            bool volSurgeBull = !double.IsNaN(AvgVolSMA) && latestVolume > VolumeBullMult * AvgVolSMA;
            bool volSurgeBear = !double.IsNaN(AvgVolSMA) && latestVolume > VolumeBearMult * AvgVolSMA;

            bool vixBull = !double.IsNaN(vixEMASlow) && vixEMAFast < vixEMASlow;
            bool vixBear = !double.IsNaN(vixEMASlow) && vixEMAFast > vixEMASlow;

            bool bullIndicator = emaBull && rsiBull && vixBull && volSurgeBull;
            bool bearIndicator = emaBear && rsiBear && vixBear && volSurgeBear;

            bool bullOverPower = emaGap > BullOverPowerThreshold;
            bool bearOverPower = emaGap < BearOverPowerThreshold;

            bool obvBearDivergence = emaBull && rsiBull && vixBull && ObvEMAFast.CurrentEMA < ObvEMASlow.CurrentEMA;
            bool bearDivergence = obvBearDivergence && RunningSignal == 1 && RunningSignalCount >= BearDivergenceMinCount;

          

            double deltaSwing = ReferenceSpot > 0 ?  (barData.Close - ReferenceSpot) * 100 / ReferenceSpot : 0; // change

            bool upSwing = deltaSwing > UpSwingPrimaryThreshold || (RunningSignal == 1 && deltaSwing > UpSwingConfirmThreshold);
            bool downSwing = deltaSwing < DownSwingPrimaryThreshold || (RunningSignal == -1 && deltaSwing < DownSwingConfirmThreshold);

            bool isBull = bullOverPower || bullIndicator || upSwing;
            bool isBear = bearOverPower || bearIndicator || (bearDivergence && !bullIndicator && !bullOverPower) || downSwing;

            // Always preferene to Bull
            isBear = isBear && !isBull;

            int currentSignalState = isBull ? 1 : isBear ? -1 : 0;

            // Reference Point Changes for every  bull & bear
            if (isBull || isBear)
            {
              
                ReferenceSpot = barData.Close;
                ReferenceTime = barData.DateTimeDT;
            }


            if (currentSignalState > 0 && RunningSignal != 1 || currentSignalState < 0 && RunningSignal != -1)
            {
                // Bull resets running signal
                RunningSignalCount = 1;
                MostFavSpot = barData.Close;
                RunningSignal = currentSignalState > 0 ? 1 : -1;
            }
            else
            {
                // bear resets RunningSignal
                RunningSignalCount++;
                MostFavSpot = RunningSignal == 1 ? Math.Max(barData.Close, MostFavSpot) : Math.Min(barData.Close, MostFavSpot);
            }




            PrevSignal = Signal;
            Signal = isBull ? 1 : isBear ? -1 : 0;
            //Signal = 0;
            LogTrendSignals(barData, emaGap, deltaSwing, upSwing, downSwing, isBull, isBear, bearDivergence);


        }

        private bool SetCustomTradeType(IBarData barData ,out TradeType customTradeType)
        {

            //expMul = 1;


            customTradeType = TradeType.None;

            bool noPosition = !TradeRecords.Any(trade => trade.Status == Status.Open);
            int openBull = TradeRecords.Count(tr => tr.Status == Status.Open && tr.Signal > 0);
            int openBear = TradeRecords.Count(tr => tr.Status == Status.Open && tr.Signal < 0);
            int bullX = openBull - openBear;
      
            int currentSignalState = (int)Signal;
            if (currentSignalState == 0)
                return false;
        
            bool pyramid = barData.DateTimeDT.Date != LastTradeDT.Date || PrevSignal != Signal;
            if (currentSignalState == LastTradedSignal && !pyramid)
                return false;


            if (Math.Abs(EMAGap.LastOrDefault()) < EMAGapMinFilter)
                return false;

            double signedAdxCustom = AdxCustomSpot.CurrentADX * (AdxCustomSpot.CurrentPlusDI >= AdxCustomSpot.CurrentMinusDI ? 1 : -1);
            double signedAdxDaily =  AdxDailySpot.CurrentTempADX * (AdxDailySpot.CurrentTempPlusDI >= AdxDailySpot.CurrentTempMinusDI ? 1.0 : -1.0);
            

            if (currentSignalState * signedAdxCustom < 0 || currentSignalState * signedAdxDaily < 0)
                return false;



            // Moved to StrategyParameters.cs: SdvAdxCustom, SdvAdxDaily, ScaleAdxDaily

            double normAdxCustom = signedAdxCustom / SdvAdxCustom;
            double normAdxDaily = signedAdxDaily / SdvAdxDaily;

            double adxComb = Math.Abs(normAdxCustom + normAdxDaily * ScaleAdxDaily);
            
            int adxContb = adxComb < AdxCombTier1 ? -2 : adxComb < AdxCombTier2 ? -1 : adxComb < AdxCombTier3 ? 0 : 1;   // (20%, 40%, 80%) - Percentiles
            int vixContb = CurrentBarVIX > VixTier1 ? 0 : CurrentBarVIX > VixTier2 ? 1 : CurrentBarVIX > VixTier3 ? 2 : 3;
            int maxOpenDirection = vixContb + adxContb;
           
            if (currentSignalState == 1 && bullX >= maxOpenDirection)
                return false;

            if (currentSignalState == -1 && -bullX >= maxOpenDirection)
                return false;


            customTradeType = currentSignalState > 0 ? TradeType.BullLadder : TradeType.BearLadder;

            LogTrendSignals();
            return true;

        


        }


        public void LogTrendSignals()
        {


            if (_snt == null)   return;
            _snt.WriteLine(IndicatorValues);
            _snt.Flush();


        }


        public void LogTrendSignals(IBarData barData,                                     
                                    double emaGap,
                                    double deltaSwing, 
                                    bool upSwing, bool downSwing, 
                                    bool isBull, bool isBear, 
                                    bool bearDivergence)
        {


            if (_sn == null)
                return;

            double timeSinceLastTrade = (barData.DateTimeDT - LastTradeDT).TotalDays;
            bool noPosition = !TradeRecords.Any(t => t.Status == Status.Open);

            IndicatorValues = $"{barData.DateTimeDT}," +
                              $"{barData.Close:F2}," +
                              $"{CustomBarFutVolumes.Last():F0}," +
                              $"{AvgVolSMA:F0}," +
                              $"{EMAFast[0]:F2}," +
                              $"{EMASlow[0]:F2}," +
                              $"{emaGap:F4}," +
                              $"{(AdxDailySpot.CurrentTempADX * (AdxDailySpot.CurrentTempPlusDI >= AdxDailySpot.CurrentTempMinusDI ? 1.0 : -1.0)):F2}," +
                              $"{((AdxCustomSpot.CurrentPlusDI > AdxCustomSpot.CurrentMinusDI ? 1 : -1) * AdxCustomSpot[0]):F2}," +
                              $"{RSI[0]:F2}," +
                              $"{CustomBarVixCloses.Last():F2}," +
                              $"{VixEMAFast[0]:F2}," +
                              $"{VixEMASlow[0]:F2}," +
                              $"{ObvEMAFast[0]:F2}," +
                              $"{ObvEMASlow[0]:F2}," +
                              $"{RunningSignal}," +
                              $"{(upSwing ? 1 : 0)}," +
                              $"{(downSwing ? 1 : 0)}," +
                              $"{(bearDivergence ? 1 : 0)}," +
                              $"{(isBull ? 1 : 0)}," +
                              $"{(isBear ? 1 : 0)}," +
                              $"{MostFavSpot:F2}," +
                              $"{ReferenceSpot:F2}," +
                              $"{ReferenceTime}," +
                              $"{deltaSwing:F2}";



            _sn.WriteLine(IndicatorValues);
            _sn.Flush();


        }



    }
}


