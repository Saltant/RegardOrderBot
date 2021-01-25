using System;
using System.Collections.Generic;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RegardOrderBot.Interfaces;
using RegardOrderBot.POCO;
using AngleSharp;
using AngleSharp.Html.Parser;
using System.Threading.Tasks;
using System.Linq;
using System.Threading;
using AngleSharp.Dom;
using System.Globalization;
using AngleSharp.Html.Dom;

namespace RegardOrderBot
{
	public class RegardParser : IParser
	{
		readonly ILogger<RegardParser> logger;
		readonly IBrowsingContext context;
		readonly IHtmlParser parser;
		readonly OrderBot orderBot;
		readonly Dictionary<int, CancellationTokenSource> trackingProducts = new Dictionary<int, CancellationTokenSource>();
		const string PRODUCT_LINK = "https://www.regard.ru/catalog/tovar";
		const string PRODUCT_STATUS = "goodCard_inStock_button";
		const string PRODUCT_NAME = "goods_head";
		const string PRODUCT_IS_SOLD_OUT = "товар распродан";
		const string PRODUCT_IN_STOCK = "в наличии";
		List<Product> products;
		public Dictionary<int, CancellationTokenSource> TrackingProducts
		{
			get
			{
				return trackingProducts;
			}
		}
		public RegardParser(ILogger<RegardParser> logger, IHostedService hostedService)
		{
			this.logger = logger;
			context = BrowsingContext.New(Configuration.Default.WithDefaultLoader().WithDefaultCookies());
			parser = context.GetService<IHtmlParser>();
			orderBot = (OrderBot)hostedService;
		}

		public bool? Start()
		{
			bool? result = null;
			try
			{
				if (GetProducts())
				{
					result = Initialize();
				}
				else
				{
					result = false;
					logger.LogInformation($"[{DateTime.Now:dd.MM.yyy HH.mm.ss}] Нет товаров для отслеживания!");
					orderBot.Host.StopApplication();
				}
			}
			catch (Exception ex)
			{
				logger.LogError(ex.Message);
			}

			return result;
		}

		bool Initialize()
		{
			bool result = false;
			products.ForEach((product) =>
			{
				CancellationTokenSource cts = new CancellationTokenSource();
				Task.Run(async () => await TrackProduct(product, cts)).ContinueWith((tracked) => 
				{
					Console.WriteLine($"End of Task with result: [{tracked.Result}]");
				});
				trackingProducts.Add(product.ArtNumber, cts);
			});
			return result;
		}

		async Task<TrackedStatus> TrackProduct(Product product, CancellationTokenSource token)
		{
			logger.LogInformation($"[{DateTime.Now:dd.MM.yyy HH.mm.ss}] Отслеживаю товар ID: {product.ArtNumber} с максимальной ценой: {product.MaxPrice.ToString("C", OrderBot.culture)}");
			TrackedStatus trackedStatus = TrackedStatus.Active;
			while (!token.IsCancellationRequested)
			{
				IDocument document = await context.OpenAsync($"{PRODUCT_LINK}{product.ArtNumber}htm");
				string cook = document.Cookie.Split("PHPSESSID=")[1];
				string productName = document.All.Where(x => x.HasAttribute("id") && x.GetAttribute("id").Equals(PRODUCT_NAME)).Select(x => x.TextContent).FirstOrDefault();
				string productStatus = document.All.Where(x => x.HasAttribute("class") && x.GetAttribute("class").Contains(PRODUCT_STATUS)).Select(x => x.TextContent).FirstOrDefault();
				
				if(productStatus == PRODUCT_IN_STOCK)
				{
					var productToken = document.All.Where(x => x.HasAttribute("name") && x.GetAttribute("name").Equals("token")).Select(x => x.Attributes.GetNamedItem("value").Value).FirstOrDefault();
					string price = document.All.Where(x => x.HasAttribute("itemprop") && x.GetAttribute("itemprop").Equals("price")).Select(x => x.Attributes.GetNamedItem("content").Value).FirstOrDefault();
					if(IsMaxPriceCheck(price, product.MaxPrice, out double currentPrice))
					{
						product.ProductName = productName;
						product.CurrentPrice = currentPrice;
						OrderResult orderResult = await orderBot.OrderProduct(product, productToken, cook);
						if(orderResult.TrackedStatus == TrackedStatus.ProductOrdered)
						{
							IHtmlDocument orderDocument = parser.ParseDocument(orderResult.Content);
							string orderNumber = orderDocument.All.Where(x => x.HasAttribute("class") && x.GetAttribute("class").Equals("green")).Select(x => x.TextContent).FirstOrDefault();
							if (!string.IsNullOrEmpty(orderNumber))
							{
								trackedStatus = orderResult.TrackedStatus;
								orderBot.ProductSuccessfulOrdered(product, orderNumber);
							}
						}
						else if(orderResult.TrackedStatus == TrackedStatus.FailOrderProcess)
						{
							logger.LogCritical($"[{DateTime.Now:dd.MM.yyy HH.mm.ss}] В процессе заказа товара [{product.ArtNumber}] {productName} возникла ошибка (статус код http запроса не в диапазоне 200-299)");
						}
					}
					else
					{
						logger.LogInformation($"[{DateTime.Now:dd.MM.yyy HH.mm.ss}] товар [{product.ArtNumber}] {productName} в наличии но его цена {price} руб. [заданная максимальная цена товара: {product.MaxPrice} руб.]");
					}
				}
				else
				{
					logger.LogDebug($"[{product.ArtNumber}] {productName} - {productStatus}");
				}
				Task.Delay(5000).Wait();
			}
			return await Task.FromResult(trackedStatus);
		}

		bool IsMaxPriceCheck(string price, int maxPrice, out double currentPrice)
		{
			currentPrice = double.Parse(price, OrderBot.culture);
			return maxPrice - currentPrice >= 0;
		}

		bool GetProducts()
		{
			if (orderBot == null)
				throw new Exception($"Ошибка! не удалось получить сервис {nameof(OrderBot)}");

			products = orderBot.Products;
			if (products.Count > 0)
				return true;
			return false;
		}

		public enum TrackedStatus
		{
			Active,
			InOrderProcess,
			FailOrderProcess,
			ProductOrdered
		}
	}
}
