#region Using declarations
using NinjaTrader.Gui.Chart;
#endregion

namespace NinjaTrader.NinjaScript.ChartStyles
{
	public class SizeMapNullStyle : ChartStyle
	{
		public override int GetBarPaintWidth(int barWidth) => barWidth;

		public override void OnRender(ChartControl chartControl, ChartScale chartScale, ChartBars chartBars)
		{
			// draw nothing
		}

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Name           = "SizeMap Null (no bars)";
				ChartStyleType = (ChartStyleType) 1770;
				BarWidth       = 1;
			}
		}
	}
}
