using System.Globalization;
using System.Xml;

namespace ECommerceBatteryShop.Services.StockSync;

/// <summary>
/// The Akakçe product feed: &lt;products&gt;&lt;product&gt;&lt;sku/&gt;...&lt;quantity/&gt;&lt;/product&gt;&lt;/products&gt;.
/// Streams the document and reads only sku and quantity, so multi-MB feeds with HTML descriptions stay cheap.
/// </summary>
public sealed class AkakceXmlFeedParser : IStockFeedParser
{
    public string Format => "akakce-xml";

    public async Task<IReadOnlyList<FeedStockItem>> ParseAsync(Stream feed, CancellationToken ct = default)
    {
        var items = new List<FeedStockItem>();
        var settings = new XmlReaderSettings { Async = true, IgnoreComments = true, DtdProcessing = DtdProcessing.Prohibit };
        using var reader = XmlReader.Create(feed, settings);

        while (await reader.ReadAsync())
        {
            ct.ThrowIfCancellationRequested();
            if (reader.NodeType != XmlNodeType.Element || reader.Name != "product")
            {
                continue;
            }

            string? sku = null;
            string? quantity = null;
            using (var product = reader.ReadSubtree())
            {
                await product.ReadAsync(); // the <product> element itself
                await product.ReadAsync(); // its first child
                // ReadElementContent/Skip already advance the reader, so only call ReadAsync for other nodes;
                // otherwise a sibling directly after </sku> would be skipped when the feed has no whitespace.
                while (!product.EOF)
                {
                    if (product.NodeType != XmlNodeType.Element || product.Depth != 1)
                    {
                        await product.ReadAsync();
                    }
                    else if (product.Name == "sku")
                    {
                        sku = (await product.ReadElementContentAsStringAsync()).Trim();
                    }
                    else if (product.Name == "quantity")
                    {
                        quantity = (await product.ReadElementContentAsStringAsync()).Trim();
                    }
                    else
                    {
                        await product.SkipAsync(); // description, images... skipped whole
                    }
                }
            }

            if (string.IsNullOrEmpty(sku) ||
                !int.TryParse(quantity, NumberStyles.Integer, CultureInfo.InvariantCulture, out var qty))
            {
                continue;
            }

            items.Add(new FeedStockItem(sku, Math.Max(0, qty)));
        }

        return items;
    }
}
