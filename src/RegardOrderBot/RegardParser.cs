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
		const string PRODUCT_STATUS = "goodCard_inStock_button";
		const string PRODUCT_NAME = "goods_head";
		const string PRODUCT_IN_STOCK = "в наличии";
		List<Product> products;
		public const string PRODUCT_LINK = "https://www.regard.ru/catalog/tovar";
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
					Product trackedProduct = tracked.Result;
					switch (trackedProduct?.TrackedStatus)
					{
						case TrackedStatus.ProductNotFound:
							logger.LogError($"Отслеживание товара: [{product.ArtNumber}]{product.ProductName} - Завершилось с ошибкой. " +
								$"(Товар не найден)");
							break;
						case TrackedStatus.FailOrderProcess:
							logger.LogError($"Отслеживание товара: [{product.ArtNumber}]{product.ProductName} - Завершилось с ошибкой. " +
								$"(Не удалось заказать товар)");
							break;
						case TrackedStatus.ProductOrdered:
							logger.LogInformation($"Отслеживание товара: [{product.ArtNumber}]{product.ProductName} - Успешно завершено.\n" +
								$"Цена заказа составила: {product.CurrentPrice}");
							break;
					}
				});
				trackingProducts.Add(product.ArtNumber, cts);
				result = true;
			});
			return result;
		}

		async Task<Product> TrackProduct(Product product, CancellationTokenSource token)
		{
			logger.LogInformation($"[{DateTime.Now:dd.MM.yyy HH.mm.ss}] Отслеживаю товар ID: {product.ArtNumber} с максимальной ценой: {product.MaxPrice.ToString("C", OrderBot.culture)}");
			product.TrackedStatus = TrackedStatus.Active;
			DateTime maxPriceCheckTimestamp = DateTime.Now;
			bool isFirstAttempt = true;
			while (!token.IsCancellationRequested)
			{
				IDocument document = await context.OpenAsync($"{PRODUCT_LINK}{product.ArtNumber}htm");
				bool productNotFound = document.All.Where(x => x.HasAttribute("class") && x.GetAttribute("class").Equals("top")).Select(x => x.TextContent).Any(x => x.Contains("Товар не найден"));
				if (productNotFound)
				{
					product.TrackedStatus = TrackedStatus.ProductNotFound;
					break;
				}
				string cookie = document.Cookie.Split("PHPSESSID=")[1];
				string productName = document.All.Where(x => x.HasAttribute("id") && x.GetAttribute("id").Equals(PRODUCT_NAME)).Select(x => x.TextContent).FirstOrDefault();
				string productStatus = document.All.Where(x => x.HasAttribute("class") && x.GetAttribute("class").Contains(PRODUCT_STATUS)).Select(x => x.TextContent).FirstOrDefault();
				product.ProductName = productName;
				if(productStatus == PRODUCT_IN_STOCK)
				{
					var productToken = document.All.Where(x => x.HasAttribute("name") && x.GetAttribute("name").Equals("token")).Select(x => x.Attributes.GetNamedItem("value").Value).FirstOrDefault();
					string price = document.All.Where(x => x.HasAttribute("itemprop") && x.GetAttribute("itemprop").Equals("price")).Select(x => x.Attributes.GetNamedItem("content").Value).FirstOrDefault();
					if(IsMaxPriceCheck(price, product.MaxPrice, out double currentPrice))
					{
						product.CurrentPrice = currentPrice;
						OrderResult orderResult = await orderBot.OrderProduct(product, productToken, cookie);
						if(orderResult.TrackedStatus == TrackedStatus.ProductOrdered)
						{
							IHtmlDocument orderDocument = parser.ParseDocument(orderResult.Content);
							string orderNumber = orderDocument.All.Where(x => x.HasAttribute("class") && x.GetAttribute("class").Equals("green")).Select(x => x.TextContent).FirstOrDefault();
							if (!string.IsNullOrEmpty(orderNumber))
							{
								product.TrackedStatus = orderResult.TrackedStatus;
								orderBot.ProductSuccessfulOrdered(product, orderNumber);
							}
						}
						else if(orderResult.TrackedStatus == TrackedStatus.FailOrderProcess)
							logger.LogCritical($"[{DateTime.Now:dd.MM.yyy HH.mm.ss}] В процессе заказа товара [{product.ArtNumber}] {productName} возникла ошибка (код https запроса не в диапазоне 200-299)");
					}
					else
					{
						product.CurrentPrice = currentPrice;
						await orderBot.ProductPriceLargerMaxPrice(product, maxPriceCheckTimestamp, isFirstAttempt).ContinueWith((task) => 
						{
							if (task.Result)
							{
								isFirstAttempt = false;
								maxPriceCheckTimestamp = DateTime.Now;
							}
						});
					}
				}
				if(!token.IsCancellationRequested)
					Task.Delay(5000).Wait();
			}
			return await Task.FromResult(product);
		}

		static bool IsMaxPriceCheck(string price, int maxPrice, out double currentPrice)
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
			None,
			Active,
			ProductNotFound,
			InOrderProcess,
			FailOrderProcess,
			ProductOrdered
		}
	}
}
