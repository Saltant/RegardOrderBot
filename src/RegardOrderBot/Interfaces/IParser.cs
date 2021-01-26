using System.Collections.Generic;
using System.Threading;

namespace RegardOrderBot.Interfaces
{
	interface IParser
	{
		bool? Start();
		Dictionary<int, CancellationTokenSource> TrackingProducts {get;}
	}
}
