using MathNet.Numerics.Integration;
using QX.BackTesting.Strategies;
using QX.FinLib.Common;
using QX.FinLib.Data;
using QX.FinLib.Instrument;
using QX.FinLib.TS;
using QX.FinLib.TSOptions;
using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Linq;
using static QX.BackTesting.Strategies.VoxLadder;

namespace QX.OptionsBTTestApp.RD
{
    public enum TradeType
    {
        None,
        BullLadder,
        BearLadder,
        BullSquareOff,
        BearSquareOff

    }

    public enum Status
    {
        Open,
        Closed,
    }
 
    public struct OptionsWithFilledPriceX
    {
        public OptionsInstrument Option { get; }
        public double FilledPriceX { get; }

        public OptionsWithFilledPriceX(OptionsInstrument option, double filledPriceX)
        {
            Option = option;
            FilledPriceX = filledPriceX;
        }
    }

    public class OptionInfo
    {
        public OptionsInstrument Option { get; set; }
        public OptionType OptionType { get; set; }
        public int baseLots { get; set; }
        public OrderSide OrderSide { get; set; }
        public double EntryPrice { get; set; }
        public double CurrentPrice { get; set; }
        public double CurrentPnL { get; set; }
        public bool isSpreadLeg { get; set; }
        public Status Status { get; set; }
    }

    public class TradeRecord
    {
        public readonly IOptionCommandAccessor OptionCommandAccessor;

        public BlackScholesCalculator BlackScholesCalculator;
        public DateTime EntryTime { get; set; }
        public DateTime ExitTime { get; set; }
        public DateTime PartialExitTime { get; set; }
        public double PnlAtPartialExit {  get; set; }
        public ExitType ExitType { get; set; }
        public double UnderlyingAtEntry { get; set; }
        public double UnderlyingAtExit { get; set; }
        public double VixAtEntry { get; set; }       
        public TradeType TradeType { get; set; }
        public DateTime ExpiryDT { get; set; }
        public double DaysToExp { get; set; }
        public double MarginUsed { get; set; }
        public List<OptionInfo> OptionsInfo { get; set; }       
        public double MostFavValue { get; set; }        
        public double TransactionCost { get; set; }
        public double SlippageCost { get; set; }
        public Status Status { get; set; }
        public TradeSetup BenchmarkRow { get; set; }
        public int SpreadLots { get; set; }
        public double MaxProfit { get; set; }
        public double MaxLoss { get; set; }
        public double SpreadCredit { get; set; }
        public double ProbProfitSpread { get; set; }
        public int LotSize { get; set; }
        public int StrikeGap { get; set; }
        public int Signal { get; set; }
        public int PyramidCount { get; set; }
        public int BullXCount { get; set; }
       

        public TradeRecord(IOptionCommandAccessor optionCommandAccessor, BlackScholesCalculator blackScholesCalculator)
        {
            BlackScholesCalculator = blackScholesCalculator;
            OptionCommandAccessor = optionCommandAccessor;
            OptionsInfo = new List<OptionInfo>();
            ExitType = ExitType.None;
            Status = Status.Open;
        }


        public void UpdateOptionsInfo(IBarData barData, double? indiaVix = null, bool allorNoneBlackScholes = false)
        {
            if (OptionsInfo == null || OptionsInfo.Count == 0)
                return;

            var openOptions = OptionsInfo.Where(o => o.Status == Status.Open).ToList();

            if (!openOptions.Any())
                return;

            var marketPrices = openOptions.Select(o => OptionCommandAccessor.GetOptionPremium(o.Option)).ToList();
            bool anyNaN = marketPrices.Any(double.IsNaN);

            foreach (var optionInfo in openOptions)
            {

                if (optionInfo.Status == Status.Closed)
                    continue;

                if (allorNoneBlackScholes)
                {
                    optionInfo.CurrentPrice = anyNaN ? BlackScholesCalculator.GetOptionBlackScholesPrice(optionInfo.Option, barData.Close, barData.DateTimeDT, indiaVix: indiaVix)
                                                     : OptionCommandAccessor.GetOptionPremium(optionInfo.Option, lookbackTolerance: 10);
                }
                else
                {
                    double optCurrentPremium = OptionCommandAccessor.GetOptionPremium(optionInfo.Option, lookbackTolerance: 10);
                    if (double.IsNaN(optCurrentPremium))
                        optCurrentPremium = BlackScholesCalculator.GetOptionBlackScholesPrice(optionInfo.Option, barData.Close, barData.DateTimeDT, indiaVix: indiaVix);

                    optionInfo.CurrentPrice = optCurrentPremium;
                }

                optionInfo.CurrentPnL = optionInfo.OrderSide == OrderSide.Buy
                                                ? (optionInfo.CurrentPrice - optionInfo.EntryPrice) * optionInfo.baseLots * SpreadLots * LotSize
                                                : (optionInfo.EntryPrice - optionInfo.CurrentPrice) * optionInfo.baseLots * SpreadLots * LotSize;

            }
        }

       

        public void UpdateMostFavValue(IBarData barData)
        {
            MostFavValue = Signal > 0 ? Math.Max(barData.Close, MostFavValue) : Math.Min(barData.Close, MostFavValue);

        }


		/// <summary>
		/// RealisedPnL : PnL of all closed options or booked farbuy leg, 
		///             : Equals to 0 if not booked
		/// UnrealisedPnL : PnL of all open Options
		///               : Equals CurrentPnL if far leg is Open, equals Spread PnL if PArtial Exit has occurred
		/// SpreadPnL : PnL of Credit Spread (irrespective of open or close)
		/// CurrentPnL : PnL of The htree options (irrespective of open or close)
		/// </summary>

		public double CurrentPnL =>	OptionsInfo?.Sum(o => o.CurrentPnL) ?? 0.0;
		public double UnRealisedPnL =>  OptionsInfo?.Where(o => o.Status == Status.Open).Sum(o => o.CurrentPnL) ?? 0.0;
        public double SpreadPnL => OptionsInfo?.Where(o => o.isSpreadLeg).Sum(o => o.CurrentPnL) ?? 0.0;
		public double RealisedPnL => OptionsInfo.Where(o => o.Status == Status.Closed).Sum(o => o.CurrentPnL);
		public bool IsPartiallyBooked => OptionsInfo?.Any(o => o.Status == Status.Closed) == true &&  OptionsInfo?.Any(o => o.Status == Status.Open) == true;


    }

    public class TradeRecordManager
    {
        private readonly IOptionCommandAccessor OptionCommandAccessor;
        private readonly BlackScholesCalculator BlackScholesCalculator;

        public TradeRecordManager(IOptionCommandAccessor optionCommandAccessor, BlackScholesCalculator blackScholesCalculator)
        {
            OptionCommandAccessor = optionCommandAccessor;
            BlackScholesCalculator = blackScholesCalculator;
        }

        public TradeRecord CreateTradeRecord()
        {
            return new TradeRecord(OptionCommandAccessor, BlackScholesCalculator);
        }


        public List<TradeRecord> FilterTradesByOffset(List<TradeRecord> tradeRecordsList,
                                                      double currentUnderlying,
                                                      double? minOffset = null,
                                                      double? maxOffset = null,
                                                      bool UseMaxPremiumOption = true)
        {
            if (minOffset == null && maxOffset == null)
                return tradeRecordsList;


            if (tradeRecordsList == null || !tradeRecordsList.Any())
                return new List<TradeRecord>();

            List<TradeRecord> filteredTrades = new List<TradeRecord>();

            foreach (var tradeRecord in tradeRecordsList)
            {
                if (tradeRecord.OptionsInfo == null || !tradeRecord.OptionsInfo.Any())
                    continue;

                OptionInfo selectedOptInfo = UseMaxPremiumOption
                    ? tradeRecord.OptionsInfo.OrderByDescending(o => o.CurrentPrice).First()
                    : tradeRecord.OptionsInfo.OrderBy(o => o.CurrentPrice).First();

                if (selectedOptInfo.Option == null)
                    continue;

                var option = selectedOptInfo.Option;
                var optionType = selectedOptInfo.OptionType;

                double dst = option.StrikePrice - currentUnderlying;
                int sgn = (optionType == OptionType.CE) ? 1 : -1;

                double outputOffset = sgn * dst / tradeRecord.StrikeGap;

                if ((!minOffset.HasValue || outputOffset >= minOffset.Value) && (!maxOffset.HasValue || outputOffset <= maxOffset.Value))
                    filteredTrades.Add(tradeRecord);
            }

            return filteredTrades;
        }


        public double GetCumulativePnLTradeRecords(List<TradeRecord> TradeRecords)
        {
            if (TradeRecords == null || !TradeRecords.Any())
                return 0;

            return TradeRecords.Sum(tradeRecord => tradeRecord.CurrentPnL);
        }

        public void RefreshTradeRecords(List<TradeRecord> TradeRecords, IBarData barData, double? indiaVix = null, bool allOrNoneBlackScholes = false)
        {
            if (TradeRecords == null || !TradeRecords.Any())
                return;

            var openTradeRecords = TradeRecords.Where(tr => tr.Status == Status.Open);


            foreach (var tradeRecord in openTradeRecords)
            {
                tradeRecord.UpdateOptionsInfo(barData, indiaVix: indiaVix, allOrNoneBlackScholes);
                tradeRecord.UpdateMostFavValue(barData);
            }
        }


        public List<OptionsWithQuantity> GetNetOpenOptionsWithQty(List<TradeRecord> tradeRecords)
        {
            if (tradeRecords == null || !tradeRecords.Any())
                return new List<OptionsWithQuantity>();

            var optionsWithQuantities = tradeRecords
                .Where(tr => tr.Status == Status.Open)
                .SelectMany(tr => tr.OptionsInfo
                    .Where(oi => oi.Option != null && oi.Status == Status.Open)
                    .Select(oi => new
                    {
                        oi.Option,
                        Qty = (oi.OrderSide == OrderSide.Buy ? 1.0 : -1.0)
                              * oi.baseLots
                              * tr.SpreadLots
                              * tr.LotSize
                    }))
                .GroupBy(x => x.Option)
                .Select(g => new OptionsWithQuantity(
                    option: g.Key,
                    quantity: g.Sum(x => x.Qty)
                ))
                .Where(x => Math.Abs(x.Quantity) > 1e-9)  // optional: drop near-zero positions
                .ToList();

            return optionsWithQuantities;
        }

        public double GetTotalCurrentPnL(List<TradeRecord> tradeRecords)
        {
            if (tradeRecords == null || !tradeRecords.Any())
                return 0.0;

            return tradeRecords.Sum(tr => tr.CurrentPnL);
        }



    }
}