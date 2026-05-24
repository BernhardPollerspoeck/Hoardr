using System.Xml.Linq;
using Hoardr.Core.Data;
using Microsoft.AspNetCore.DataProtection.Repositories;
using SproutDB.Core;
using static Hoardr.Core.Data.Sprout;

namespace Hoardr.Web.Auth;

/// <summary>
/// Data Protection key store backed by SproutDB (table <c>dp_keys</c>), so cookie keys
/// persist across restarts without writing key files to disk.
/// </summary>
public sealed class SproutXmlRepository(ISproutDatabase db) : IXmlRepository
{
    public IReadOnlyCollection<XElement> GetAllElements()
    {
        var data = db.Exec("get dp_keys").Data;
        if (data is not { Count: > 0 })
            return [];

        var elements = new List<XElement>(data.Count);
        foreach (var row in data)
        {
            var xml = row.Str("xml");
            if (!string.IsNullOrEmpty(xml))
                elements.Add(XElement.Parse(xml));
        }
        return elements.AsReadOnly();
    }

    public void StoreElement(XElement element, string friendlyName)
    {
        var name = string.IsNullOrEmpty(friendlyName) ? Guid.NewGuid().ToString("N") : friendlyName;
        var xml = element.ToString(SaveOptions.DisableFormatting);

        var existing = db.Exec($"get dp_keys where friendly_name = {Q(name)}").Data;
        if (existing is { Count: > 0 })
            db.Exec($"upsert dp_keys {{_id: {existing[0].U64("_id")}, xml: {Q(xml)}}}");
        else
            db.Exec($"upsert dp_keys {{friendly_name: {Q(name)}, xml: {Q(xml)}}}");
    }
}
