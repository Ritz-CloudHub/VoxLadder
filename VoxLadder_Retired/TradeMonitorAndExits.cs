using MathNet.Numerics.Optimization;
using QX.BackTestingCore;
using QX.FinLib.Common;
using QX.FinLib.Data;
using QX.OptionsBTTestApp.RD;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using static QX.BackTesting.Strategies.VoxLadder;
using static System.Net.Mime.MediaTypeNames;

namespace QX.BackTesting.Strategies
{
    public partial class VoxLadder
    {
        public enum ExitType
        {
            None,
            Expired,
            SwingExit,
            StopLoss,
            StopLossEarlyExit,
            ProfitBook,
            ProfitBookEarlyExit,
            StopLossExitAbs,
            ReversalExit
            
        }       

       
        private void MonitorAndExitOpenTrades(IBarData barData)
        {
            var openTrades = TradeRecords.Where(t => t.Status == Status.Open).ToList();
            if (!openTrades.Any())
                return;

            var toExit = new List<TradeRecord>();

        
            foreach (var trade in openTrades)
            {

                double creditUpperbound = CreditUpperBound * SpreadLotsPerTrade;

                double credit = trade.SpreadCredit;
                if (credit > creditUpperbound)
                    credit = creditUpperbound;


                double slf;
                bool slExitCond = false;

                double swingExitPct;
                bool swingExitCond = false;


                if (trade.IsPartiallyBooked)
                {
                    double realisedPartExit = trade.RealisedPnL / credit;

                    if (realisedPartExit > 0)
                    {
                        swingExitPct = SwingExitPct;
                        slf = StopLossFactorPartialProfit;
                        slExitCond = trade.CurrentPnL < -1 * slf * credit;

                    }
                    else
                    {
                        swingExitPct = SwingExitPct;
                        slf = StopLossFactorPartialLossBase + realisedPartExit;
                        slExitCond = trade.SpreadPnL < -1 * slf * credit;
                    }
                }
                else
                {
                    swingExitPct = SwingExitPct;
                    slf = StopLossFactor;
                    slExitCond = trade.CurrentPnL < -1 * slf * credit;
                }



                if (slExitCond)
                {
                    trade.ExitType = ExitType.StopLoss;
                    toExit.Add(trade);
                    LogExit(trade, barData, $"STOP-LOSS)");
                    continue;
                }
               

             


                swingExitCond = Math.Abs((trade.MostFavValue - barData.Close)) * 100 / trade.MostFavValue > swingExitPct;

                if (swingExitCond)
                {
                    trade.ExitType = ExitType.SwingExit;
                    toExit.Add(trade);
                    LogExit(trade, barData, $"Reversal");
                    continue;
                }


            }


            
            
            if (toExit.Any())
            {                

                SquareOffGivenTrades(toExit, barData, "Before Expiry EXIT");
            }
                
        }
      

        public void TryBookSingleLeg(IBarData barData)
        {

            var openTrades = TradeRecords.Where(t => t.Status == Status.Open && t.BenchmarkRow != null).ToList();
            if (!openTrades.Any())
                return;

          
            var toBookFarLeg = new List<TradeRecord>();

           
            foreach (var trade in openTrades)
            {

                double daysLeftToExpiry = (trade.ExpiryDT - barData.DateTimeDT).TotalDays;

                foreach (var optInfo in trade.OptionsInfo)
                {

                    if (optInfo.isSpreadLeg || optInfo.Status == Status.Closed)
                        continue;



                   double pbPrice = Math.Max(optInfo.EntryPrice + FarLegProfitBookAdd, FarLegProfitBookMult * optInfo.EntryPrice);
                   double slPrice = Math.Max(optInfo.EntryPrice - FarLegStopLossSub, FarLegStopLossMult * optInfo.EntryPrice);

                   bool optPriceCond = optInfo.CurrentPrice > pbPrice || optInfo.CurrentPrice < slPrice;
                   bool spotPriceCond = (trade.TradeType == TradeType.BullLadder ? 1 : -1) * (barData.Close - trade.UnderlyingAtEntry) * 100 / trade.UnderlyingAtEntry > FarLegSpotMoveThreshold;
                    
                    bool bookOk = optPriceCond || spotPriceCond;

                    if (bookOk)
                    {

                        toBookFarLeg.Add(trade);
                        break;
                    }




                }
            }

            if (toBookFarLeg.Any())
                SquareOffBuyLegs(toBookFarLeg, barData);

        }
        
        public void SettleTradesAtExchange(IBarData barData)
        {
            var CurrentExpiryTrades = TradeRecords.Where(trade => trade.ExpiryDT.Date == CurrentExpiryDT.Date && trade.Status == Status.Open).ToList();           
            SquareOffGivenTrades(CurrentExpiryTrades, barData, "Exchange Settlement", settleAtIntrinsicValue: true, ExitType.Expired);
        }

        public void SquareOffGivenTrades(List<TradeRecord> tradesToSquareOff, IBarData barData, string str ,bool settleAtIntrinsicValue = false, ExitType exitType = ExitType.None)
        {

            if (tradesToSquareOff == null || !tradesToSquareOff.Any())
                return;

            double transCost;
            double slippageCost;
            double thisTradeTransCost;
            double thisTradeSlippageCost;

            double tradedPrice;
            bool success;
            double transCostPct;

            if (dbg)
            {
                int x = 0;
            }

            foreach (var trade in tradesToSquareOff)
            {
                if (trade.Status == Status.Closed)
                    continue;

                thisTradeTransCost = 0;
                thisTradeSlippageCost = 0;

                foreach (var optInfo in trade.OptionsInfo)
                {
                    Debug.Assert(optInfo.Option != null);

                    if (optInfo.Status == Status.Closed)
                        continue;


                    if (optInfo.OrderSide == OrderSide.Buy)
                    {
                        if (settleAtIntrinsicValue)
                            success = EnterShortAtIntrinsic(optInfo.Option, optInfo.baseLots * trade.SpreadLots, barData, out tradedPrice, str);
                        else
                            success = EnterShortAtMarketX(optInfo.Option, optInfo.baseLots * trade.SpreadLots, barData, out tradedPrice, str);

                        Debug.Assert(success);
                        transCostPct = TransactionCostSellPct;

                    }
                    else
                    {
                        if (settleAtIntrinsicValue)
                            success = EnterLongAtIntrinsic(optInfo.Option, optInfo.baseLots * trade.SpreadLots, barData, out tradedPrice, str);
                        else
                            success = EnterLongAtMarketX(optInfo.Option, optInfo.baseLots * trade.SpreadLots, barData, out tradedPrice, str);

                        Debug.Assert(success);
                        transCostPct = TransactionCostBuyPct;
                    }

                    optInfo.CurrentPrice = tradedPrice;

                    optInfo.CurrentPnL = optInfo.OrderSide == OrderSide.Buy
                        ? (tradedPrice - optInfo.EntryPrice) * optInfo.baseLots * trade.SpreadLots * LotSize
                        : -1 * (tradedPrice - optInfo.EntryPrice) * optInfo.baseLots * trade.SpreadLots * LotSize;

                 

                    optInfo.Status = Status.Closed;


                    var slippageConfig = new SlippageStructure(ExchangeIndexPair, tradedPrice, DTX);
                    double spreadSlippagePct = slippageConfig.GetSlippagePercentage();
                    slippageCost = optInfo.baseLots * trade.SpreadLots * LotSize * tradedPrice * spreadSlippagePct * 0.01;
                    transCost = optInfo.baseLots * trade.SpreadLots * LotSize * tradedPrice * transCostPct * 0.01;

                    if (!settleAtIntrinsicValue)
                    {
                        thisTradeTransCost += transCost;
                        thisTradeSlippageCost += slippageCost;

                        DailyTransactionCost += transCost;
                        ExpiryTransactionCost += transCost;
                        CycleTransactionCost += transCost;

                        DailySlippageCost += slippageCost;
                        ExpirySlippageCost += slippageCost;
                        CycleSlippageCost += slippageCost;
                    }

                }


                //trade.UpdateTradeRecordPnL();

                if (exitType != ExitType.None)
                    trade.ExitType = exitType;

                trade.TransactionCost += thisTradeTransCost;
                trade.SlippageCost += thisTradeSlippageCost;

                trade.ExitTime = barData.DateTimeDT;               
                trade.UnderlyingAtExit = barData.Close;              
                trade.Status = Status.Closed;

                DailyMargin -= trade.MarginUsed;

            }
        }

        public void SquareOffBuyLegs(List<TradeRecord> tradesToBookBuyLeg, IBarData barData)
        {

            if (tradesToBookBuyLeg == null || !tradesToBookBuyLeg.Any())
                return;
          

            foreach (var trade in tradesToBookBuyLeg)
            {
                if (trade.Status == Status.Closed)
                    continue;

                double thisTradeTransCost = 0;
                double thisTradeSlippageCost = 0;

                foreach (var optInfo in trade.OptionsInfo)
                {
                    Debug.Assert(optInfo.Option != null);

                    if (optInfo.Status == Status.Closed || optInfo.isSpreadLeg)
                        continue;

                    Debug.Assert(EnterShortAtMarketX(optInfo.Option, optInfo.baseLots * trade.SpreadLots, barData, out double tradedPrice, message: "Buy Leg SquareOff"));
                    
                    optInfo.CurrentPrice = tradedPrice;
                    optInfo.CurrentPnL = (tradedPrice - optInfo.EntryPrice) * optInfo.baseLots * trade.SpreadLots * LotSize;                    
                    optInfo.Status = Status.Closed; // close only farbuyLeg


                    var slippageConfig = new SlippageStructure(ExchangeIndexPair, tradedPrice, DTX);
                    double spreadSlippagePct = slippageConfig.GetSlippagePercentage();
                    double slippageCost = optInfo.baseLots * trade.SpreadLots * LotSize * tradedPrice * spreadSlippagePct * 0.01;
                    double transCost = optInfo.baseLots * trade.SpreadLots * LotSize * tradedPrice * TransactionCostBuyPct * 0.01;

                    thisTradeTransCost += transCost;
                    thisTradeSlippageCost += slippageCost;

                    DailyTransactionCost += transCost;
                    ExpiryTransactionCost += transCost;
                    CycleTransactionCost += transCost;

                    DailySlippageCost += slippageCost;
                    ExpirySlippageCost += slippageCost;
                    CycleSlippageCost += slippageCost;

                }


                //trade.UpdateTradeRecordPnL();
                trade.TransactionCost += thisTradeTransCost;
                trade.SlippageCost += thisTradeSlippageCost;               
                trade.PartialExitTime = barData.DateTimeDT;
                trade.PnlAtPartialExit = trade.CurrentPnL;
                
            }

        }

        private void LogExit(TradeRecord trade, IBarData barData, string reason)
        {
            _sl?.WriteLine($"{barData.DateTimeDT:yyyy-MM-dd HH:mm},{trade.TradeType},{trade.SpreadLots}L,{trade.CurrentPnL:F0},{reason}");
            _sl?.Flush();
        }
    }
}