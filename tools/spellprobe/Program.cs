using WriteLite.Services;
var a = new RuleBasedAnalyzer();
var text = "слово  слово";
var issues = a.Analyze(text);
Console.WriteLine("count="+issues.Count);
foreach (var i in issues) Console.WriteLine(i.Original+"=>"+i.Replacement+" cat="+i.Category+" conf="+i.Confidence+" rule="+i.RuleId);
