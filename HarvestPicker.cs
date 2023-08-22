using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using HarvestPicker.Api.Request;
using HarvestPicker.Api.Response;
using Newtonsoft.Json;
using SharpDX;

namespace HarvestPicker;

public class HarvestPrices
{
    public double YellowJuiceValue;
    public double PurpleJuiceValue;
    public double BlueJuiceValue;
    public double WhiteJuiceValue;
}

public class HarvestPicker : BaseSettingsPlugin<HarvestPickerSettings>
{
    private const string DefaultQuery =
        "query Query($search: LivePricingSummarySearch!) {livePricingSummarySearch(search: $search) {entries {itemGroup {key}valuation{value}}}}";

    public override bool Initialise()
    {
        _pricesGetter = LoadPricesFromDisk();
        Settings.ReloadPrices.OnPressed = () => { _pricesGetter = LoadPricesFromDisk(); };
        return true;
    }

    private readonly Stopwatch _lastRetrieveStopwatch = new Stopwatch();
    private Task _pricesGetter;
    private HarvestPrices _prices;
    private List<((Entity, double), (Entity, double))> _irrigatorPairs;
    private string CachePath => Path.Join(ConfigDirectory, "pricecache.json");

    private HarvestPrices Prices
    {
        get
        {
            if (_pricesGetter is { IsCompleted: true })
            {
                _pricesGetter = null;
            }

            if ((!_lastRetrieveStopwatch.IsRunning ||
                 _lastRetrieveStopwatch.Elapsed >= TimeSpan.FromMinutes(Settings.PriceRefreshPeriodMinutes)) &&
                _pricesGetter == null)
            {
                _pricesGetter = FetchPrices();
                _lastRetrieveStopwatch.Reset();
            }

            return _prices;
        }
    }

    private async Task FetchPrices()
    {
        await Task.Yield();
        try
        {
            Log("Starting data update");
            using var client = new HttpClient();
            var response = await client.PostAsync("https://api.poestack.com/graphql",
                new StringContent(JsonConvert.SerializeObject(new RequestRoot
                {
                    operationName = "Query",
                    variables = new Variables
                    {
                        search = new Search
                        {
                            league = Settings.League ?? throw new Exception("Please configure the league"),
                            offSet = 0,
                            searchString = "lifeforce",
                            quantityMin = 1,
                            tag = "currency",
                        },
                    },
                    query = DefaultQuery,
                }), Encoding.Default, "application/json"));
            response.EnsureSuccessStatusCode();
            var str = await response.Content.ReadAsStringAsync();
            var responseObject = JsonConvert.DeserializeObject<ResponseRoot>(str);
            if (responseObject.errors != null)
            {
                Log($"Request returned errors: {responseObject.errors}");
                throw new Exception("Request returned errors");
            }

            var dataMap = responseObject.data.livePricingSummarySearch.entries.ToDictionary(x => x.itemGroup.key, x => (float?)x.valuation.value);
            if (dataMap.Any(x => x.Value is 0 or null) || dataMap.Count < 4)
            {
                Log($"Some data is missing: {str}");
            }

            _prices = new HarvestPrices
            {
                BlueJuiceValue = dataMap.GetValueOrDefault("primal crystallised lifeforce") ?? 0,
                YellowJuiceValue = dataMap.GetValueOrDefault("vivid crystallised lifeforce") ?? 0,
                PurpleJuiceValue = dataMap.GetValueOrDefault("wild crystallised lifeforce") ?? 0,
                WhiteJuiceValue = dataMap.GetValueOrDefault("sacred crystallised lifeforce") ?? 0,
            };
            await File.WriteAllTextAsync(CachePath, JsonConvert.SerializeObject(_prices));
            Log("Data update complete");
        }
        catch (Exception ex)
        {
            DebugWindow.LogError(ex.ToString());
        }
        finally
        {
            _lastRetrieveStopwatch.Restart();
        }
    }

    private async Task LoadPricesFromDisk()
    {
        await Task.Yield();
        try
        {
            Log("Loading data from disk");
            var cachePath = CachePath;
            if (File.Exists(cachePath))
            {
                _prices = JsonConvert.DeserializeObject<HarvestPrices>(await File.ReadAllTextAsync(cachePath));
                Log("Data loaded from disk");
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(cachePath) < TimeSpan.FromMinutes(Settings.PriceRefreshPeriodMinutes))
                {
                    _lastRetrieveStopwatch.Restart();
                }
            }
            else
            {
                Log("Cached data doesn't exist");
            }
        }
        catch (Exception ex)
        {
            DebugWindow.LogError(ex.ToString());
        }
    }


    private void Log(string message)
    {
        LogMessage($"[HarvestPicker] {message}");
    }

    public override Job Tick()
    {
        _irrigatorPairs = new List<((Entity, double), (Entity, double))>();
        var irrigators = GameController.EntityListWrapper.ValidEntitiesByType[EntityType.MiscellaneousObjects]
            .Where(x => x.Path == "Metadata/MiscellaneousObjects/Harvest/Irrigator" &&
                        x.TryGetComponent<StateMachine>(out var stateMachine) &&
                        stateMachine.States.FirstOrDefault(s => s.Name == "colour")?.Value is { } and not 0).ToList();
        var irrigatorPairs = new List<((Entity, double), (Entity, double))>();
        while (irrigators.LastOrDefault() is { } entity1)
        {
            irrigators.RemoveAt(irrigators.Count - 1);
            var closestIrrigator = irrigators.MinBy(x => x.Distance(entity1));
            if (closestIrrigator == null || closestIrrigator.Distance(entity1) > 85)
            {
                irrigatorPairs.Add(((entity1, CalculateIrrigatorValue(entity1)), (default, default)));
            }
            else
            {
                irrigators.Remove(closestIrrigator);
                irrigatorPairs.Add(((entity1, CalculateIrrigatorValue(entity1)), (closestIrrigator, CalculateIrrigatorValue(closestIrrigator))));
            }
        }

        _irrigatorPairs = irrigatorPairs;
        return null;
    }

    private double CalculateIrrigatorValue(Entity e)
    {
        var prices = Prices;
        if (prices == null)
        {
            LogMessage("Prices are still not loaded, unable to calculate values");
            return 0;
        }

        if (!e.TryGetComponent<HarvestWorldObject>(out var harvest))
        {
            Log($"Entity {e} has no harvest component");
            return 0;
        }

        double TypeToPrice(int type) => type switch
        {
            1 => prices.PurpleJuiceValue,
            2 => prices.YellowJuiceValue,
            3 => prices.BlueJuiceValue,
            _ => LogWrongType(type),
        };

        var seeds = harvest.Seeds;
        if (seeds.Any(x => x.Seed == null))
        {
            Log("Some seeds have no associated dat file");
            return 0;
        }

        return seeds.Sum(seed => seed.Seed.Tier switch
        {
            1 => Settings.SeedsPerT1Plant * TypeToPrice(seed.Seed.Type),
            2 => Settings.SeedsPerT2Plant * TypeToPrice(seed.Seed.Type),
            3 => Settings.SeedsPerT3Plant * TypeToPrice(seed.Seed.Type),
            4 => Settings.SeedsPerT4Plant * TypeToPrice(seed.Seed.Type) + Settings.T4PlantWhiteSeedChance * prices.WhiteJuiceValue,
            var tier => LogWrongTier(tier),
        } * seed.Count);
    }

    private double LogWrongType(int type)
    {
        Log($"Seed had unknown type {type}");
        return 0;
    }

    private double LogWrongTier(int tier)
    {
        Log($"Seed had unknown tier {tier}");
        return 0;
    }

    public void DrawIrrigatorValue(Entity e, double value, Color color)
    {
        var text = $"Value: {value:F1}";
        var textPosition = GameController.IngameState.Camera.WorldToScreen(e.PosNum);
        Graphics.DrawBox(textPosition, textPosition + Graphics.MeasureText(text), Color.Black);
        Graphics.DrawText(text, textPosition, color);
    }

    public override void Render()
    {
        foreach (var ((irrigator1, value1), (irrigator2, value2)) in _irrigatorPairs)
        {
            if (irrigator2 == null)
            {
                DrawIrrigatorValue(irrigator1, value1, Settings.NeutralColor);
            }
            else
            {
                var (color1, color2) = value1.CompareTo(value2) switch
                {
                    > 0 => (Settings.GoodColor, Settings.BadColor),
                    0 => (Settings.NeutralColor, Settings.NeutralColor),
                    < 0 => (Settings.BadColor, Settings.GoodColor),
                };
                DrawIrrigatorValue(irrigator1, value1, color1);
                DrawIrrigatorValue(irrigator2, value2, color2);
            }
        }
    }
}