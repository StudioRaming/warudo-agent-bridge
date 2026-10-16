using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace StudioRaming.WarudoAgentBridge
{
    // ui.capture: one screenshot with a numbered box over every control ui.list returns for the area, colored by kind.
    // The numbers are the positions in ui.list order (same filters, same region), so the MCP server can check them again later.
    public sealed partial class AgentBridgePlugin
    {
        static readonly string[] UiKindNames = { "button", "toggle", "slider", "input", "scroll", "other" };

        static Color32 UiKindColor(string kind)
        {
            switch (kind)
            {
                case "button": return new Color32(255, 170, 0, 255);
                case "toggle": return new Color32(0, 210, 255, 255);
                case "slider": return new Color32(255, 60, 200, 255);
                case "input": return new Color32(90, 255, 90, 255);
                case "scroll": return new Color32(170, 130, 255, 255);
                default: return new Color32(235, 235, 235, 255);
            }
        }

        static readonly Dictionary<string, string> UiKindColorNames = new Dictionary<string, string>
        {
            { "button", "orange" }, { "toggle", "cyan" }, { "slider", "magenta" }, { "input", "green" }, { "scroll", "violet" }, { "other", "light grey" },
        };

        async UniTask<object> UiCapture(JObject p)
        {
            int timeoutMs = OptionalTimeout(p);
            CaptureOptions options = CaptureOptions.Parse(p);
            string query = ((string)p["query"] ?? "").Trim();
            bool interactableOnly = (bool?)p["interactableOnly"] ?? true;
            int limit = CaptureOptions.OptInt(p, "limit", 60, 1, 200);
            string path = ScreenEvidencePath();
            if (File.Exists(path)) throw new InvalidOperationException("SCREEN_CAPTURE_OUTPUT_EXISTS: request evidence path already exists.");

            long started = Stamp();
            GrabbedFrame grabbed = await GrabFrame(timeoutMs);
            double grabMs = Ms(started);
            long processStart = Stamp();
            int width = grabbed.Image.Width, height = grabbed.Image.Height;
            CropPlan crop = ResolveCrop(options, width, height);

            // The controls are read at the end of the frame the pixels came from, so boxes and pixels agree.
            UiElement[] matches = FilterUiElements(width, height, query, interactableOnly, null, new[] { crop.X, crop.Y, crop.W, crop.H }).ToArray();
            UiElement[] shown = matches.Take(limit).ToArray();

            Pixmap raw = grabbed.Image.Crop(crop.X, crop.Y, crop.W, crop.H);
            CaptureMapping mapping = PanelMapping(width, height, crop, options.MaxSize, options.MaxSize);
            Pixmap image = raw.Downscale(mapping.ImageW, mapping.ImageH);
            if (options.Grid) DrawGrid(image, mapping, options.GridStep);
            DrawUiBoxes(image, mapping, shown);
            double processMs = Ms(processStart);

            JObject result = SaveCapture(path, image, mapping, raw, grabbed, grabMs, processMs);
            result["crop"] = crop.ToJson();
            if (crop.Around != null) result["around"] = crop.Around;
            result["screen"] = new JObject { ["width"] = width, ["height"] = height };
            result["query"] = query;
            result["interactableOnly"] = interactableOnly;
            result["limit"] = limit;
            result["total"] = matches.Length;
            result["shown"] = shown.Length;
            result["truncated"] = matches.Length > shown.Length;
            result["kinds"] = JObject.FromObject(UiKindColorNames);
            var elements = new JArray();
            for (int index = 0; index < shown.Length; index++)
            {
                var item = new JObject { ["n"] = index + 1 };
                foreach (JProperty property in JObject.FromObject(DescribeUiElement(shown[index])).Properties()) item.Add(property.Name, property.Value);
                elements.Add(item);
            }
            result["elements"] = elements;
            return result;
        }

        // Big boxes first so small controls stay on top of the panels that hold them; the number tags go last.
        void DrawUiBoxes(Pixmap image, CaptureMapping m, UiElement[] elements)
        {
            int textScale = CaptureTextScale(image.Width, image.Height);
            int thickness = Math.Max(2, textScale);
            var boxes = new int[elements.Length][];
            for (int i = 0; i < elements.Length; i++)
            {
                Rect rect = elements[i].Rect;
                int x0 = (int)Math.Round(m.ImageX(rect.xMin)), y0 = (int)Math.Round(m.ImageY(rect.yMin));
                int x1 = (int)Math.Round(m.ImageX(rect.xMax)), y1 = (int)Math.Round(m.ImageY(rect.yMax));
                x0 = Math.Max(m.ViewX, x0); y0 = Math.Max(m.ViewY, y0);
                x1 = Math.Min(m.ViewX + m.ViewW, x1); y1 = Math.Min(m.ViewY + m.ViewH, y1);
                boxes[i] = new[] { x0, y0, Math.Max(1, x1 - x0), Math.Max(1, y1 - y0) };
            }
            foreach (int i in Enumerable.Range(0, elements.Length).OrderByDescending(index => (long)boxes[index][2] * boxes[index][3]))
                image.Outline(boxes[i][0], boxes[i][1], boxes[i][2], boxes[i][3], thickness, UiKindColor(UiKindOf(elements[i])));
            var ink = new Color32(0, 0, 0, 255);
            for (int i = 0; i < elements.Length; i++)
            {
                string label = (i + 1).ToString();
                int tagWidth = Pixmap.TextWidth(label, textScale) + textScale * 4, tagHeight = Pixmap.TextHeight(textScale) + textScale * 4;
                int tagX = boxes[i][0], tagY = boxes[i][1] - tagHeight;
                if (tagY < m.ViewY) tagY = boxes[i][1];
                if (tagX + tagWidth > m.ViewX + m.ViewW) tagX = m.ViewX + m.ViewW - tagWidth;
                image.DrawText(tagX + textScale * 2, tagY + textScale * 2, label, textScale, ink, UiKindColor(UiKindOf(elements[i])));
            }
        }
    }
}
