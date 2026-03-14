using Accord.Statistics.Kernels;
using MathNet.Numerics.Distributions;
using QX.Common.Helper;
using QX.FinLib.Common;
using QX.FinLib.Data;
using QX.FinLib.Data.TI;
using QX.FinLib.Instrument;
using QX.FinLib.TSOptions;
using System;

namespace QX.BackTesting.Strategies
{
    /// <summary>
    /// All IVs are in decimal
    /// All Time To Expiry are in Years
    /// 
    /// V - Volatility (Annualized standard deviation of log returns)
    /// V - indiaVix value can be used
    /// V - If we know the market Price of option calculate IV from ImpliedVolatility method
    /// 
    /// S  - Spot (Current price of the underlying asset)
    /// K  - StrikePrice (Exercise price of the option)
    /// Tm - Time to Expiry in years (Time remaining until option expiration)
    /// r  - Risk free rate of Interest (default 0; Annualized continuous risk-free rate)
    /// q  - Dividend yield (default 0; Continuous dividend yield of the underlying asset)
    ///  
    /// optionType - Call/Put (OptionType.CE for call, OptionType.PE for put)
    /// Tol - Tolerance (Precision for convergence in implied volatility)
    /// MaxIter - Maximum Iterations (Limit for bisection in implied volatility)
    /// OptionPrice - Observed market price of the option   
    /// Low - Lower bound for volatility search
    /// High - Upper bound for volatility search
    /// Mid - Midpoint volatility in bisection
    /// PriceMid - Calculated price at midpoint volatility
    /// Price - Computed option price
    /// Delta - Option Delta Greek
    /// Gamma - Option Gamma Greek
    /// Vega - Option Vega Greek
    /// Theta - Option Theta Greek
    /// Rho - Option Rho Greek
    /// D1 - First Black-Scholes parameter (d1)
    /// D2 - Second Black-Scholes parameter (d2)
    /// IV - Implied volatility
    /// </summary>
    public class BlackScholesCalculator
    {
        private readonly IOptionCommandAccessor OptionCommandAccessor;
        private static readonly TimeSpan DefaultExpiryTime = new TimeSpan(15, 29, 0); // Default to NSE closing time

        // Constructor to inject dependencies
        public BlackScholesCalculator(IOptionCommandAccessor optionCommandAccessor)
        {
            OptionCommandAccessor = optionCommandAccessor ?? throw new ArgumentNullException(nameof(optionCommandAccessor));
        }

        private static double StableNormalCDF(double x)
        {
            if (x > 8) // Right tail approximation
            {
                double z = x * x;
                return 1.0 - Math.Exp(-z / 2) / (x * Math.Sqrt(2 * Math.PI)) * (1 - 1 / z);
            }
            if (x < -8) // Left tail approximation
            {
                double z = x * x;
                return Math.Exp(-z / 2) / (-x * Math.Sqrt(2 * Math.PI)) * (1 - 1 / z);
            }
            return Normal.CDF(0, 1, x); // Use MathNet for non-extreme cases
        }
       
        private static double StableNormalPDF(double x)
        {
            if (Math.Abs(x) > 8) // Approximation for extreme tails
            {
                double z = x * x;
                return Math.Exp(-z / 2) / Math.Sqrt(2 * Math.PI);
            }
            return Normal.PDF(0, 1, x); // Use MathNet for non-extreme cases
        }

        private static double AdaptiveZCutoff(double Tm)
        {
            const double Z_NEAR = 8.0;
            const double Z_FAR = 10.0;
            const double TAU = 2.0 / 365.0; // 2 days
            return Z_NEAR + (Z_FAR - Z_NEAR) * (Tm / (Tm + TAU));
        }

        private static bool HasNoOptionality(double S, double K, double Tm, double V)
        {
            const double MIN_VOL_TIME = 1e-6;

            double volTime = V * Math.Sqrt(Tm);
            if (volTime < MIN_VOL_TIME)
                return true;

            double z = Math.Abs(Math.Log(S / K)) / volTime;
            return z >= AdaptiveZCutoff(Tm);
        }

        private static double BlackScholesDelta(OptionType optionType, double S, double K, double Tm, double V, double r = 0, double q = 0)
        {
            if (S <= 0 || K <= 0 || V <= 0 || Tm < 0)
                throw new ArgumentException("Invalid inputs");

            // Intrinsic delta limit
            if (HasNoOptionality(S, K, Tm, V))
            {
                if (optionType == OptionType.CE)
                    return S > K ? 1.0 : 0.0;
                else
                    return S < K ? -1.0 : 0.0;
            }

            double volTime = V * Math.Sqrt(Tm);
            double D1 = (Math.Log(S / K) + (r - q + 0.5 * V * V) * Tm) / volTime;

            if (optionType == OptionType.CE)
                return Math.Exp(-q * Tm) * StableNormalCDF(D1);
            else
                return -Math.Exp(-q * Tm) * StableNormalCDF(-D1);
        }

        private static double BlackScholesGamma(double S, double K, double Tm, double V, double r = 0, double q = 0)
        {
            if (S <= 0 || K <= 0 || V <= 0 || Tm < 0)
                throw new ArgumentException("Invalid inputs");

            if (HasNoOptionality(S, K, Tm, V))
                return 0.0;

            double volTime = V * Math.Sqrt(Tm);
            double D1 = (Math.Log(S / K) + (r - q + 0.5 * V * V) * Tm) / volTime;

            return Math.Exp(-q * Tm) * StableNormalPDF(D1) / (S * volTime);
        }

        private static double BlackScholesVega(double S, double K, double Tm, double V, double r = 0, double q = 0)
        {
            if (S <= 0 || K <= 0 || V <= 0 || Tm < 0)
                throw new ArgumentException("Invalid inputs");

            if (HasNoOptionality(S, K, Tm, V))
                return 0.0;

            double volTime = V * Math.Sqrt(Tm);
            double D1 = (Math.Log(S / K) + (r - q + 0.5 * V * V) * Tm) / volTime;

            return S * Math.Exp(-q * Tm) * StableNormalPDF(D1) * Math.Sqrt(Tm);
        }

        private static double BlackScholesTheta(OptionType optionType, double S, double K, double Tm, double V, double r = 0, double q = 0)
        {
            if (S <= 0 || K <= 0 || V <= 0 || Tm < 0)
                throw new ArgumentException("Invalid inputs");

            if (HasNoOptionality(S, K, Tm, V))
                return 0.0;

            double volTime = V * Math.Sqrt(Tm);
            double D1 = (Math.Log(S / K) + (r - q + 0.5 * V * V) * Tm) / volTime;
            double D2 = D1 - volTime;

            double common =
                -(S * Math.Exp(-q * Tm) * StableNormalPDF(D1) * V) / (2 * Math.Sqrt(Tm));

            if (optionType == OptionType.CE)
            {
                return common
                     - r * K * Math.Exp(-r * Tm) * StableNormalCDF(D2)
                     + q * S * Math.Exp(-q * Tm) * StableNormalCDF(D1);
            }
            else
            {
                return common
                     + r * K * Math.Exp(-r * Tm) * StableNormalCDF(-D2)
                     - q * S * Math.Exp(-q * Tm) * StableNormalCDF(-D1);
            }
        }

        private static double BlackScholesRho(OptionType optionType, double S, double K, double Tm, double V, double r = 0, double q = 0)
        {
            if (S <= 0 || K <= 0 || V <= 0 || Tm < 0)
                throw new ArgumentException("Invalid inputs");

            if (HasNoOptionality(S, K, Tm, V))
                return 0.0;

            double volTime = V * Math.Sqrt(Tm);
            double D1 = (Math.Log(S / K) + (r - q + 0.5 * V * V) * Tm) / volTime;
            double D2 = D1 - volTime;

            if (optionType == OptionType.CE)
                return K * Tm * Math.Exp(-r * Tm) * StableNormalCDF(D2);
            else
                return -K * Tm * Math.Exp(-r * Tm) * StableNormalCDF(-D2);
        }

        private static double BlackScholesPrice(OptionType optionType, double S, double K, double Tm, double V, double r = 0, double q = 0)
        {
            const double MIN_TICK = 0.05;

            if (S <= 0 || K <= 0 || V <= 0 || Tm < 0)
                throw new ArgumentException("Invalid inputs");

            // --- Intrinsic (with NSE tick protection)
            double intrinsic = optionType == OptionType.CE ? Math.Max(S - K, MIN_TICK) : Math.Max(K - S, MIN_TICK);

            // --- Reachability / degeneracy collapse
            if (HasNoOptionality(S, K, Tm, V))
                return intrinsic;

            // --- Standard Black–Scholes
            double volTime = V * Math.Sqrt(Tm);

            double D1 = (Math.Log(S / K)  + (r - q + 0.5 * V * V) * Tm)  / volTime;
            double D2 = D1 - volTime;

            double price;
            if (optionType == OptionType.CE)
            {
                price = S * Math.Exp(-q * Tm) * StableNormalCDF(D1)
                      - K * Math.Exp(-r * Tm) * StableNormalCDF(D2);
            }
            else
            {
                price = K * Math.Exp(-r * Tm) * StableNormalCDF(-D2)
                      - S * Math.Exp(-q * Tm) * StableNormalCDF(-D1);
            }

            // --- Never below intrinsic
            return Math.Max(price, intrinsic);
        }

        private static double BlackScholesIV(OptionType optionType, double optionPrice, double S, double K, double Tm, double r = 0, double q = 0)
        {
            const double MIN_IV = 0.001;
            const double MAX_IV = 5.0;
            const double MIN_TICK = 0.05;

            const int MaxIter = 200;
            const double Tol = 1e-6;

            if (S <= 0 || K <= 0 || Tm < 0)
                return double.NaN;

            // --- Intrinsic
            double intrinsic = optionType == OptionType.CE
                ? Math.Max(S - K, MIN_TICK)
                : Math.Max(K - S, MIN_TICK);

            // --- If market price itself is intrinsic → no optionality → IV undefined
            if (optionPrice <= intrinsic + Tol)
                return 0.0;   // explicit "no optionality" signal

            // --- If model says no optionality → IV meaningless
            if (HasNoOptionality(S, K, Tm, 0.20)) // seed vol only
                return 0.0;

            // --- Initial bounds
            double low = MIN_IV;
            double high = MAX_IV;
            double V = 0.20; // seed

            // ---------- Phase 1: Newton–Raphson ----------
            int newtonMaxIter = Math.Min(20, MaxIter);
            for (int i = 0; i < newtonMaxIter; i++)
            {
                double price = BlackScholesPrice(optionType, S, K, Tm, V, r, q);
                double diff = price - optionPrice;

                if (Math.Abs(diff) < Tol || Math.Abs(diff / optionPrice) < Tol)
                    return V;

                double vega = BlackScholesVega(S, K, Tm, V, r, q);

                // Vega collapse → stop Newton
                if (vega < 1e-8)
                    break;

                V -= diff / vega;
                V = Math.Max(low, Math.Min(high, V));
            }

            // ---------- Phase 2: Bisection ----------
            for (int i = newtonMaxIter; i < MaxIter; i++)
            {
                double mid = 0.5 * (low + high);
                double priceMid = BlackScholesPrice(optionType, S, K, Tm, mid, r, q);
                double diffMid = priceMid - optionPrice;

                if (Math.Abs(diffMid) < Tol || Math.Abs(diffMid / optionPrice) < Tol)
                    return mid;

                if (priceMid < optionPrice)
                    low = mid;
                else
                    high = mid;
            }

            return double.NaN;
        }

        private static double BlackScholesStrike(OptionType optionType, double targetPrice, double S, double Tm, double atm_IV, double r = 0, double q = 0, double Tol = 1e-6, int MaxIter = 200)
        {
            if (targetPrice <= 0 || S <= 0 || atm_IV <= 0 || Tm < 0)
            {
                if (targetPrice <= 0)
                {
                    return double.NaN;
                }
                return double.NaN;
            }

            if (Tm < 1e-4)
            {
                if (optionType == OptionType.CE)
                {
                    if (targetPrice >= S) return double.NaN;
                    return S - targetPrice;
                }
                else
                {
                    return S + targetPrice;
                }
            }

            double Low = 0.001 * S;
            double High = 10 * S;
            double K = S;


            double V = 0;
            double price = 0;

            // Phase 1: Newton-Raphson with limited iterations, adjusting IV per K
            int newtonMaxIter = 20;
            for (int i = 0; i < newtonMaxIter; i++)
            {
               

                V =  SmileCurveIV(atm_IV, Tm, S, K);
                price = BlackScholesPrice(optionType, S, K, Tm, V, r, q);
                
                double D1 = (Math.Log(S / K) + (r - q + 0.5 * V * V) * Tm) / (V * Math.Sqrt(Tm));
                double D2 = D1 - V * Math.Sqrt(Tm);
                double dualDelta;
                if (optionType == OptionType.CE)
                {
                    dualDelta = -Math.Exp(-r * Tm) * StableNormalCDF(D2);
                }
                else
                {
                    dualDelta = Math.Exp(-r * Tm) * StableNormalCDF(-D2);
                }
                double diff = price - targetPrice;
                if (Math.Abs(diff) < Tol || (targetPrice > 0 && Math.Abs(diff / targetPrice) < Tol))
                {
                    return K;
                }
                if (Math.Abs(dualDelta) < 1e-10)
                {
                    break; // Early fallback to bisection
                }
                K -= diff / dualDelta;
                K = Math.Max(Low, Math.Min(High, K)); // Clamp to prevent divergence
            }

            // Phase 2: Bisection fallback, adjusting IV per mid K
            bool isIncreasing = (optionType == OptionType.PE);
            for (int i = 0; i < MaxIter - newtonMaxIter; i++)
            {
                double mid = (Low + High) / 2;
                V = SmileCurveIV(atm_IV, Tm, S, mid);
                double priceMid = BlackScholesPrice(optionType, S, mid, Tm, V, r, q);
                double diffMid = priceMid - targetPrice;
                if (Math.Abs(diffMid) < Tol || (targetPrice  > 0 && Math.Abs(diffMid / targetPrice) < Tol))
                {
                    return mid;
                }
                if (isIncreasing)
                {
                    if (priceMid < targetPrice)
                    {
                        Low = mid;
                    }
                    else
                    {
                        High = mid;
                    }
                }
                else
                {
                    if (priceMid > targetPrice)
                    {
                        Low = mid;
                    }
                    else
                    {
                        High = mid;
                    }
                }
            }


            if (Math.Abs(price - targetPrice) < 1)
                return K;
            
            return double.NaN;
        }

        private static double GetTimeToExpiryInDays(OptionsInstrument Option, DateTime currentDT)
        {
            DateTime optExpiryDate = Option.ContractExpiration;
            DateTime optExpiryDateTime = new DateTime(optExpiryDate.Year, optExpiryDate.Month, optExpiryDate.Day).Add(DefaultExpiryTime);

            return (optExpiryDateTime - currentDT).TotalDays;
           

        }

        private static double SmileCurveIV( double atm_IV, double Tm, double S, double K)
        {

            // Tm is in years
            if (Tm * 365 * 24 <= 12) 
                Tm = 12 / (24 * 365); // clamping to 12 hrs to prevent Iv explosion


            double x = Math.Log(K / S);
            double atmVar = atm_IV * atm_IV * Tm;

            const double b = 0.19;     // curvature
            const double rho = -0.13;    // downside skew
            const double m = -0.04;    // smile center (left-shifted)
            const double sigma = 0.35;     // wing smoothness

            double sviWing = b * ( rho * (x - m) + Math.Sqrt((x - m) * (x - m) + sigma * sigma) - (rho * (-m) + Math.Sqrt(m * m + sigma * sigma)));
            double wingDamping = Math.Min(1.0, Math.Sqrt(Tm / 0.08));  // 30 / 365 ~ 0.08

            double w = atmVar + wingDamping * sviWing;
            if (w <= 0) return atm_IV;

            double IV = Math.Sqrt(w / Tm);

            // final safety clamp (relative to ATM)
            IV = Math.Min(IV, 3.0 * atm_IV); // Clamp for realism
            IV = Math.Max(IV, 0.01);

           
            return IV;
        }

        public double GetAtmIV(double underlying, DateTime currentDT, DateTime? expiryDT = null) 
        {


            // If currentDT is passed as expiryDT
            // It will get nearest Expiry's AtmOptions IV using currentDT

            expiryDT = expiryDT ?? currentDT;
            OptionCommandAccessor.GetATMOptionPair(underlying, expiryDT.Value, 0, out OptionsInstrument callOptionATM, out OptionsInstrument putOptionATM);

            if (callOptionATM == null && putOptionATM == null)
            {               
                return double.NaN;
            }

            double callIV = double.NaN;
            double putIV = double.NaN;

            if (callOptionATM != null)
            {
                double callOptionPrice = OptionCommandAccessor.GetOptionPremium(callOptionATM);

                if (!double.IsNaN(callOptionPrice))
                {
                    double callK = callOptionATM.StrikePrice;                   
                    DateTime callExpiryDateTime = callOptionATM.ContractExpiration + DefaultExpiryTime;
                    double callTTE = (callExpiryDateTime - currentDT).TotalDays;
                    double callTm = Math.Max(0, callTTE / 365.0);
                    callIV = BlackScholesIV(OptionType.CE, callOptionPrice, underlying, callK, callTm);                    
                }
               
            }

            if (putOptionATM != null)
            {
                double putOptionPrice = OptionCommandAccessor.GetOptionPremium(putOptionATM);

                if (!double.IsNaN(putOptionPrice))
                {
                    double putK = putOptionATM.StrikePrice;
                    DateTime putExpiryDateTime = putOptionATM.ContractExpiration + DefaultExpiryTime;
                    double putTTE = (putExpiryDateTime - currentDT).TotalDays;                   
                    double putTm = Math.Max(0, putTTE / 365.0);
                    putIV = BlackScholesIV(OptionType.PE, putOptionPrice, underlying, putK, putTm);
                }               
            }


            if (double.IsNaN(callIV) && double.IsNaN(putIV))
                return double.NaN;
            else if (double.IsNaN(callIV))
                return putIV;
            else if (double.IsNaN(putIV))
                return callIV;
            else
                return (callIV + putIV) / 2;
        }

        public double GetAtmIVfromIndiaVix(double indiaVix, double timeToExpiryInDays)
        {
            // --- constants (NIFTY-friendly defaults) ---
            const double lambda0 = 0.10;   // base skew premium
            const double lambda1 = 0.15;   // short-expiry skew amplification

           
            if (timeToExpiryInDays < 0.0)
                throw new ArgumentException("timeToExpiry must be non-negative.");

            if (timeToExpiryInDays == 0.0)
                return 0.01;

            // time-adjusted skew premium
            double lambda = lambda0 + lambda1 * Math.Sqrt(30.0 / timeToExpiryInDays);

            // ATM IV estimate
            double atmIv = indiaVix * 0.01 / Math.Sqrt(1.0 + lambda);  // In decimal

            return atmIv;
        }

        

        public double GetOptionDelta(OptionsInstrument Option, double underlying, DateTime currentDT, double? indiaVix = null)
        {
            if (Option == null)
                return double.NaN;


            double S = underlying;
            double K = Option.StrikePrice;
          
            double IV = GetOptionImpliedVolatility(Option, underlying, currentDT, indiaVix);

            double TTEInDays = GetTimeToExpiryInDays(Option, currentDT);
            double Tm = TTEInDays / 365.0;

            if (IV != double.NaN)
                return BlackScholesDelta(Option.OptionType, S, K, Tm, IV);
            else
                return double.NaN;          

        }
      
        public double GetOptionGamma(OptionsInstrument Option, double underlying, DateTime currentDT, double? indiaVix = null)
        {
            if (Option == null)
                return double.NaN;

            double S = underlying;
            double K = Option.StrikePrice;

           
            double IV = GetOptionImpliedVolatility(Option, underlying, currentDT, indiaVix);

            double TTEInDays = GetTimeToExpiryInDays(Option, currentDT);
            double Tm = TTEInDays / 365.0;


            if (IV != double.NaN)
                return BlackScholesGamma(S, K, Tm, IV);
            else
                return double.NaN;

        }

        public double GetOptionVega(OptionsInstrument Option, double underlying, DateTime currentDT,  double? indiaVix = null)
        {
            if (Option == null)
                return double.NaN;

            double S = underlying;
            double K = Option.StrikePrice;

           
            double IV = GetOptionImpliedVolatility(Option, underlying, currentDT, indiaVix);

            double TTEInDays = GetTimeToExpiryInDays(Option, currentDT);
            double Tm = TTEInDays / 365.0;

            if (IV != double.NaN)
                return BlackScholesVega(S, K, Tm, IV);
            else
                return double.NaN;

        }

        public double GetOptionTheta(OptionsInstrument Option, double underlying, DateTime currentDT,  double? indiaVix = null)
        {
            if (Option == null)
                return double.NaN;

            double S = underlying;
            double K = Option.StrikePrice;

            double IV = GetOptionImpliedVolatility(Option, underlying, currentDT, indiaVix);

            double TTEInDays = GetTimeToExpiryInDays(Option, currentDT);
            double Tm = TTEInDays / 365.0;

          

            if (IV != double.NaN)
                return BlackScholesTheta(Option.OptionType, S, K, Tm, IV);
            else
                return double.NaN;

           
            
        }

        public double GetOptionRho(OptionsInstrument Option, double underlying, DateTime currentDT, double? indiaVix = null)
        {
            if (Option == null)
                return double.NaN;

            double S = underlying;
            double K = Option.StrikePrice;

            double IV = GetOptionImpliedVolatility(Option, underlying, currentDT, indiaVix);

            double TTEInDays = GetTimeToExpiryInDays(Option, currentDT);
            double Tm = TTEInDays / 365.0;

            if (IV != double.NaN)
                return BlackScholesRho(Option.OptionType, S, K, Tm, IV);
            else
                return double.NaN;
           
        }
       
        public double GetOptionBlackScholesPrice(OptionsInstrument Option, double underlying, DateTime currentDT, double? indiaVix = null)
        {
           

            if (Option == null)   return double.NaN;

            double S = underlying;
            double K = Option.StrikePrice;

            double IV = GetOptionImpliedVolatility(Option, underlying, currentDT, indiaVix);

            double TTEInDays = GetTimeToExpiryInDays(Option, currentDT);
            double Tm = TTEInDays / 365.0;

            if (IV != double.NaN)
            {
                double bspRounded = Math.Round(BlackScholesPrice(Option.OptionType, S, K, Tm, IV) / 0.05) * 0.05;
                return Math.Max(bspRounded, 0.05);
            }                
            else
                return double.NaN;           
           
        }

        public double GetOptionImpliedVolatility(OptionsInstrument option, double underlying, DateTime currentDT, double? indiaVix = null, bool useSmileCurve = true)
        {


            if (option == null)
                return double.NaN;


            double S = underlying;
            double K = option.StrikePrice;
            double Tm = GetTimeToExpiryInDays(option, currentDT) / 365;
            double IV = 0;



            if (!useSmileCurve)
            {
                double optionPrice = OptionCommandAccessor.GetOptionPremium(option);

                if (!double.IsNaN(optionPrice))
                    IV = BlackScholesIV(option.OptionType, optionPrice, S, K, Tm);

                if (!double.IsNaN(IV))
                    return IV;
            }


            DateTime expiryDT = option.ContractExpiration + DefaultExpiryTime;
            double TTEinDays = Tm * 365.0;
            double atm_IV = indiaVix.HasValue ? GetAtmIVfromIndiaVix(indiaVix.Value, TTEinDays)
                                              : GetAtmIV(underlying, currentDT, expiryDT);


            return SmileCurveIV(atm_IV, Tm, S, K);


        }

        public double GetOptionBlackScholesPriceAtDateX(OptionsInstrument Option, double underlying, DateTime valuationDT, double? indiaVix = null)
        {


            if (Option == null) return double.NaN;

            double S = underlying;
            double K = Option.StrikePrice;

            double IV = GetOptionImpliedVolatility(Option, underlying, valuationDT, indiaVix);

            double TTEInDays = GetTimeToExpiryInDays(Option, valuationDT);
            double Tm = TTEInDays / 365.0;

            if (IV != double.NaN)
            {
                double bspRounded = Math.Round(BlackScholesPrice(Option.OptionType, S, K, Tm, IV) / 0.05) * 0.05;
                return Math.Max(bspRounded, 0.05);
            }
            else
                return double.NaN;

        }

        public double GetOptionImpliedVolatilityAtDateX(OptionsInstrument option, double underlying, DateTime valuationDT, double? indiaVix = null, bool useSmileCurve = true)
        {


            if (option == null)
                return double.NaN;


            double S = underlying;
            double K = option.StrikePrice;
            double Tm = GetTimeToExpiryInDays(option, valuationDT) / 365;
            double IV = 0;



            if (!useSmileCurve)
            {
                double optionPrice = OptionCommandAccessor.GetOptionPremium(option);

                if (!double.IsNaN(optionPrice))
                    IV = BlackScholesIV(option.OptionType, optionPrice, S, K, Tm);

                if (!double.IsNaN(IV))
                    return IV;
            }


            DateTime expiryDT = option.ContractExpiration + DefaultExpiryTime;
            double TTEinDays = Tm * 365.0;
            double atm_IV = indiaVix.HasValue ? GetAtmIVfromIndiaVix(indiaVix.Value, TTEinDays)
                                              : GetAtmIV(underlying, valuationDT, expiryDT);


            return SmileCurveIV(atm_IV, Tm, S, K);


        }

        public double GetStrikeFromOptionPrice(OptionType optionType, double targetPrice, double underlying, double atm_IV, double timeToExpiryInDays)
        {

            double Tm = timeToExpiryInDays / 365.0;

            if (timeToExpiryInDays <= 0)
            {
                return double.NaN;
            }

            return BlackScholesStrike(optionType, targetPrice, underlying, Tm, atm_IV);
        }

        






    }
}