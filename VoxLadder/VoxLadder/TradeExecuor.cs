using System;
using System.Collections.Generic;
using System.Diagnostics;          // <-- Debug
using System.Linq;
using QX.FinLib.Common;            // <-- OptionType lives here
using QX.FinLib.Data;
using QX.FinLib.Instrument;
using QX.BackTesting.Indicators;
using Accord.Statistics.Models.Regression.Linear;

namespace QX.BackTesting.Strategies
{
    public partial class VoxLadder
    {
        
        public bool EnterShortAtMarketX(OptionsInstrument option, int orderLot, IBarData barData, out double tradedPrice, string message, double? limitPrice = null, string message2 = null, bool ExpiredOptionIntrinsicFill = false)
        {
            tradedPrice = double.NaN;
            if (option == null || orderLot <= 0)
                return false;



            int position = OptionCommandAccessor.GetPosition(option);
            string msg = $"Short: {option.InstrumentDescription} LotQty: {orderLot},{message},{message2}";

            if (position == 0)
            {
                if (!OptionCommandAccessor.EnterShortAtMarket(option, orderLot, out tradedPrice, msg, lookbackTolerance: 5))
                {
                    //_sl.WriteLine("EnterShortAtMarket Failed:" + msg);
                    //_sl.WriteLine("Entering At BlackScholes Price for BackTest");


                    //tradedPrice = OptionCommandAccessor.GetOptionPremium(option);
                    //if (double.IsNaN(tradedPrice))
                    //    tradedPrice = GetBlackScholesPrice(option, barData);




                    tradedPrice = limitPrice ?? GetBlackScholesPrice(option, barData);

                    if (!OptionCommandAccessor.EnterShortAtGivenPrice(option, orderLot, tradedPrice, msg))
                        return false;

                }
            }
            else
            {
                int newPositionToAppend = orderLot * LotSize;
                int desiredPosition = position - newPositionToAppend;
                if (desiredPosition == 0)
                {
                    if (!OptionCommandAccessor.GoFlatAtMarket(option, out tradedPrice, $"{msg} Position Zero {option.InstrumentName}", lookbackTolerance: 5))
                    {
                        //_sl.WriteLine("DecreaseLongAtMarket Failed:" + msg);
                        //_sl.WriteLine("Entering At BlackScholes Price for BackTest");

                        //tradedPrice = OptionCommandAccessor.GetOptionPremium(option);
                        //if (double.IsNaN(tradedPrice))
                        //    tradedPrice = GetBlackScholesPrice(option, barData);


                        tradedPrice = limitPrice ?? GetBlackScholesPrice(option, barData);



                        if (!OptionCommandAccessor.GoFlatAtGivenPrice(option, tradedPrice, msg))
                            return false;
                    }
                }
                else
                {
                    int desiredLots = desiredPosition / LotSize;
                    if (!OptionCommandAccessor.SetPositionAtMarket(option, desiredLots, out tradedPrice, msg, lookbackTolerance: 5))
                    {
                        //_sl.WriteLine("DecreaseLongAtMarket Failed:" + msg);
                        //_sl.WriteLine("Entering At BlackScholes Price for BackTest");                       



                        //tradedPrice = OptionCommandAccessor.GetOptionPremium(option);
                        //if (double.IsNaN(tradedPrice))
                        //    tradedPrice = GetBlackScholesPrice(option, barData);


                        tradedPrice = limitPrice ?? GetBlackScholesPrice(option, barData);

                        if (!OptionCommandAccessor.SetPositionAtGivenPrice(option, desiredLots, tradedPrice, msg))
                            return false;
                    }
                }
            }
            return true;
        }

        public bool EnterLongAtMarketX(OptionsInstrument option, int orderLot, IBarData barData, out double tradedPrice, string message, double? limitPrice = null, string message2 = null, bool ExpiredOptionIntrinsicFill = false)
        {
            tradedPrice = double.NaN;
            if (option == null || orderLot <= 0)
                return false;


            int position = OptionCommandAccessor.GetPosition(option);
            string msg = $"Long: {option.InstrumentDescription} LotQty: {orderLot},{message},{message2}";

            if (position == 0)
            {
                if (!OptionCommandAccessor.EnterLongAtMarket(option, orderLot, out tradedPrice, msg, lookbackTolerance: 5))
                {
                    

                    tradedPrice = limitPrice ?? GetBlackScholesPrice(option, barData);

                    if (!OptionCommandAccessor.EnterLongAtGivenPrice(option, orderLot, tradedPrice, msg))
                        return false;
                }
            }
            else
            {
                int newPositionToAppend = orderLot * LotSize;
                int desiredPosition = newPositionToAppend + position;
                string logMessage = position > 0 ? "IncreaseLongAtMarket Failed:" : "DecreaseLongAtMarket Failed:";

                if (desiredPosition == 0)
                {
                    if (!OptionCommandAccessor.GoFlatAtMarket(option, out tradedPrice, $"{msg} Position Zero {option.InstrumentName}", lookbackTolerance: 5))
                    {
                        //_sl.WriteLine(logMessage + msg);
                        //_sl.WriteLine("Entering At BlackScholes Price for BackTest");

                        //tradedPrice = OptionCommandAccessor.GetOptionPremium(option);
                        //if (double.IsNaN(tradedPrice))
                        //    tradedPrice = GetBlackScholesPrice(option, barData);


                        tradedPrice = limitPrice ?? GetBlackScholesPrice(option, barData);

                        if (!OptionCommandAccessor.GoFlatAtGivenPrice(option, tradedPrice, msg))
                            return false;
                    }
                }
                else
                {
                    int desiredLots = desiredPosition / LotSize;
                    if (!OptionCommandAccessor.SetPositionAtMarket(option, desiredLots, out tradedPrice, msg, lookbackTolerance: 5))
                    {
                        //_sl.WriteLine(logMessage + msg);
                        //_sl.WriteLine("Entering At BlackScholes Price for BackTest");

                        //tradedPrice = OptionCommandAccessor.GetOptionPremium(option);
                        //if (double.IsNaN(tradedPrice))
                        //    tradedPrice = GetBlackScholesPrice(option, barData);


                        tradedPrice = limitPrice ?? GetBlackScholesPrice(option, barData);

                        if (!OptionCommandAccessor.SetPositionAtGivenPrice(option, desiredLots, tradedPrice, msg))
                            return false;
                    }
                }
            }
            return true;
        }

        public bool EnterShortAtIntrinsic(OptionsInstrument option, int orderLot, IBarData barData, out double tradedPrice, string message, string message2 = null)
        {
            tradedPrice = 0;

            if (option == null || orderLot <= 0)
                return false;

            tradedPrice = GetIntrinsicValue(option, barData);
            int position = OptionCommandAccessor.GetPosition(option);
            string msg = $"Short: {option.InstrumentDescription} LotQty: {orderLot},{message},{message2}";

            if (position == 0)
            {
                if (!OptionCommandAccessor.EnterShortAtGivenPrice(option, orderLot, tradedPrice, msg))
                    return false;
            }
            else
            {
                int newPositionToAppend = orderLot * LotSize;
                int desiredPosition = position - newPositionToAppend;
                if (desiredPosition == 0)
                {
                    if (!OptionCommandAccessor.GoFlatAtGivenPrice(option, tradedPrice, msg))
                        return false;
                }
                else
                {
                    int desiredLots = desiredPosition / LotSize;
                    if (!OptionCommandAccessor.SetPositionAtGivenPrice(option, desiredLots, tradedPrice, msg))
                        return false;
                }
            }
            return true;
        }

        public bool EnterLongAtIntrinsic(OptionsInstrument option, int orderLot, IBarData barData, out double tradedPrice, string message, string message2 = null, bool ExpiredOptionIntrinsicFill = false)
        {
            tradedPrice = 0;

            if (option == null || orderLot <= 0)
                return false;

            tradedPrice = GetIntrinsicValue(option, barData);
            int position = OptionCommandAccessor.GetPosition(option);
            string msg = $"Long: {option.InstrumentDescription} LotQty: {orderLot},{message},{message2}";

            if (position == 0)
            {
                if (!OptionCommandAccessor.EnterLongAtGivenPrice(option, orderLot, tradedPrice, msg))
                    return false;
            }
            else
            {
                int newPositionToAppend = orderLot * LotSize;
                int desiredPosition = newPositionToAppend + position;
                if (desiredPosition == 0)
                {
                    if (!OptionCommandAccessor.GoFlatAtGivenPrice(option, tradedPrice, msg))
                        return false;
                }
                else
                {
                    int desiredLots = desiredPosition / LotSize;
                    if (!OptionCommandAccessor.SetPositionAtGivenPrice(option, desiredLots, tradedPrice, msg))
                        return false;
                }
            }

            return true;
        }

        public double GetBlackScholesPrice(OptionsInstrument option, IBarData barData)
        {
            double outPremium = double.NaN;

            //DateTime OptionsExpiry = option.ContractExpiration + DayEndTime;
            //double TTE = (OptionsExpiry - barData.DateTimeDT).TotalDays;
           
            //// Special Glitch Handling
            //if (TTE < 0 && OptionsExpiry == new DateTime(2023, 06, 29, 15, 29, 00))
            //    TTE = 0;


            outPremium = BlackScholesCalculator.GetOptionBlackScholesPrice(option, barData.Close, barData.DateTimeDT, indiaVix: CurrentBarVIX);
            Debug.Assert(!double.IsNaN(outPremium));

            if (_sl != null)
            {
                _sl.WriteLine($"BlackScholesPrice for Option {option} Spot: {barData.Close} Time: {barData.DateTimeDT} is {outPremium:F2}");
                _sl.Flush();
            }



            return outPremium;

        }

        public double GetIntrinsicValue(OptionsInstrument option, IBarData barData)
        {
            return option.OptionType == OptionType.CE ? Math.Max(0.05, barData.Close - option.StrikePrice) : Math.Max(0.05, option.StrikePrice - barData.Close);
        }

   
    }
}