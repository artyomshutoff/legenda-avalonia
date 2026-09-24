using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Legenda.App;

public static class ClientExcelExporter
{
    private static readonly XNamespace Sheet = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    public static byte[] Export(IEnumerable<ClientRecord> clients)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            XNamespace types = "http://schemas.openxmlformats.org/package/2006/content-types";
            Write(zip, "[Content_Types].xml", new XElement(types + "Types",
                new XElement(types + "Default", new XAttribute("Extension", "rels"), new XAttribute("ContentType", "application/vnd.openxmlformats-package.relationships+xml")),
                new XElement(types + "Default", new XAttribute("Extension", "xml"), new XAttribute("ContentType", "application/xml")),
                new XElement(types + "Override", new XAttribute("PartName", "/xl/workbook.xml"), new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml")),
                new XElement(types + "Override", new XAttribute("PartName", "/xl/worksheets/sheet1.xml"), new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"))));
            Write(zip, "_rels/.rels", Relationships("officeDocument", "xl/workbook.xml"));
            Write(zip, "xl/_rels/workbook.xml.rels", Relationships("worksheet", "worksheets/sheet1.xml"));
            Write(zip, "xl/workbook.xml", new XElement(Sheet + "workbook",
                new XAttribute(XNamespace.Xmlns + "r", Rel),
                new XElement(Sheet + "sheets", new XElement(Sheet + "sheet",
                    new XAttribute("name", "Клиенты"), new XAttribute("sheetId", "1"), new XAttribute(Rel + "id", "rId1")))));

            var rows = new XElement(Sheet + "sheetData");
            rows.Add(Row(1, new[] { "Фамилия", "Имя", "Отчество", "Дата рождения", "Номер абонемента", "Дата покупки", "Дата окончания", "Чёрный список", "Телефон" }));
            var index = 2;
            foreach (var client in clients)
                rows.Add(Row(index++, new[] { client.LastName, client.FirstName, client.MiddleName,
                    client.BirthDate?.ToString("dd.MM.yyyy") ?? "", client.MembershipNumber,
                    client.PurchaseDate, client.ExpiryDate, client.IsBlacklisted ? "Да" : "Нет", client.Phone }));
            Write(zip, "xl/worksheets/sheet1.xml", new XElement(Sheet + "worksheet",
                new XElement(Sheet + "sheetViews", new XElement(Sheet + "sheetView", new XAttribute("workbookViewId", "0"),
                    new XElement(Sheet + "pane", new XAttribute("ySplit", "1"), new XAttribute("topLeftCell", "A2"),
                        new XAttribute("activePane", "bottomLeft"), new XAttribute("state", "frozen")))),
                new XElement(Sheet + "cols", new XElement(Sheet + "col", new XAttribute("min", "1"),
                    new XAttribute("max", "9"), new XAttribute("width", "24"), new XAttribute("customWidth", "1"))),
                rows, new XElement(Sheet + "autoFilter", new XAttribute("ref", $"A1:I{index - 1}"))));
        }
        return output.ToArray();
    }

    private static XElement Relationships(string type, string target)
    {
        XNamespace ns = "http://schemas.openxmlformats.org/package/2006/relationships";
        return new XElement(ns + "Relationships", new XElement(ns + "Relationship",
            new XAttribute("Id", "rId1"), new XAttribute("Type", Rel.NamespaceName + "/" + type), new XAttribute("Target", target)));
    }

    private static XElement Row(int index, string[] values)
    {
        var row = new XElement(Sheet + "row", new XAttribute("r", index));
        for (var i = 0; i < values.Length; i++)
        {
            // Inline strings preserve membership numbers and never execute client data as formulas.
            var text = new string(System.Array.FindAll(values[i].ToCharArray(), XmlConvert.IsXmlChar));
            row.Add(new XElement(Sheet + "c", new XAttribute("r", $"{(char)('A' + i)}{index}"), new XAttribute("t", "inlineStr"),
                new XElement(Sheet + "is", new XElement(Sheet + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), text))));
        }
        return row;
    }

    private static void Write(ZipArchive zip, string path, XElement root)
    {
        using var stream = zip.CreateEntry(path).Open();
        using var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false) });
        new XDocument(root).Save(writer);
    }
}
