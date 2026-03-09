namespace QX.BackTesting.Strategies.QuantumVaR
{
    public class HedgeCandidate
    {
        public double Strike { get; set; }
        public QX.FinLib.Instrument.OptionsInstrument Option { get; set; }
        public double Premium { get; set; }
        public int Lots { get; set; }
        public QX.FinLib.Common.OptionType Type { get; set; }
        public double Theta { get; set; }
        public double Delta { get; set; }
    }
}
