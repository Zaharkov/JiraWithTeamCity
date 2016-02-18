using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;

namespace JiraWithTC
{
    class Program
    {
        /// <summary>
        ///Параметр 0 - тип TC
        ///Параметр 1 - тип билда в TC
        ///Параметр 2 - номер бранча (если не задан,
        ///             будут взяты все что ожидают билда в jira)
        ///Параметр 3 - номер бранча для url
        ///Параметр 4 - тип TC, где проверять номер билда
        ///Параметр 5 - номер билда в ТС (для проверки прошлого билда)
        ///Парамтер 6 - если не задан - не обновлять задачи в jira
        ///             если false - обновлять только при фейле
        ///             если true - обновлять всегда
        /// </summary>
        static void Main(string[] args)
        {
            var param = new Parameters(args);
            var jira = new JiraApi("default");
            var teamCity = new TeamCityApi(param.On);

            var startBuild = true;
            if (param.NotStartBuilds.Contains(param.Type))
            {
                Console.WriteLine("Запуск билда с типом '" + param.Type + "' содержится в notstartbuilds - будет проигнорирован");
                startBuild = false;
            }

            if (param.Type == OperationType.Build)
            {
                if (!startBuild)
                    return;

                Thread.Sleep(5000);
                var branches = string.IsNullOrEmpty(param.Branch)
                    ? jira.GetWaitIssues()
                    : new List<string> { param.Branch };

                teamCity.StartBuilds(branches, param);
            }
            else
            {
                var teamCityCheck = new TeamCityApi(param.CheckOn);
                var result = teamCityCheck.CheckStatus(param.CheckBuildId);

                if (result == null && startBuild)
                    teamCity.StartBuilds(new List<string> { param.Branch }, param);

                if (param.Jira.HasValue && param.Jira.Value)
                {
                    if (result != null || param.Type == OperationType.Smoke)
                        jira.UpdateJiraStatus(param.Branch, param.BranchUrl, result, teamCityCheck.UrlToSend, param.CheckBuildId);
                }
            }
        }
    }

    public class Parameters
    {
        public OperationType Type;
        public string On;
        public string BuildType;
        public string Branch;
        public string Domain;
        public string BranchUrl;
        public string CheckOn;
        public string CheckBuildId;
        public bool? Jira;
        public List<OperationType> NotStartBuilds = new List<OperationType>();

        public BuildProperties Properties;

        private static readonly Regex BranchRegex = new Regex(@"fn-[0-9]{2,5}");

        public Parameters(IEnumerable<string> args)
        {
            var notstartbuilds = "";
            foreach (var arg in args)
            {
                var split = arg.Split('=');

                if (split.Length != 2)
                    throw new ArgumentException("Некорректный параметр: " + arg + Environment.NewLine +
                                                "Должен быть задан как 'ключ1=значение1[,значение1Б,...] ключ2=значение2[,значение2Б,...]'");

                var key = split[0].ToLower();
                var value = split[1];

                switch (key)
                {
                    case "type": Type = GetOperation(value); break;
                    case "on": On = value; break;
                    case "buildtype": BuildType = value; break;
                    case "branch": Branch = value.Replace("refs/heads/", ""); break;
                    case "domain":
                    {
                        Domain = ClearFeature(value.Replace("refs/heads/", ""));
                        BranchUrl = "http://" + Domain + ".actidev.ru";
                        break;
                    }
                    case "checkon" : CheckOn = value; break;
                    case "checkbuildid": CheckBuildId = value; break;
                    case "jira": Jira = string.IsNullOrEmpty(value) ? null : (bool?)bool.Parse(value); break;
                    case "notstartbuilds":
                    {
                        notstartbuilds = value;
                        var builds = value.Split(',');
                        foreach (var build in builds)
                        {
                            if(!string.IsNullOrEmpty(build))
                                NotStartBuilds.Add(GetOperation(build));
                        }

                        break;
                    }
                }
            }

            if(Type == 0 || string.IsNullOrEmpty(On) || string.IsNullOrEmpty(BuildType))
                throw new ArgumentException("Значения type, on, buildtype должны быть всегда заданы");

            if(Type != OperationType.Build && (string.IsNullOrEmpty(Branch) || string.IsNullOrEmpty(Domain)
                || string.IsNullOrEmpty(CheckOn) || string.IsNullOrEmpty(CheckBuildId)))
                throw new ArgumentException("Если тип отличен от 'build' то должны быть заданы branch, domain, checkon, checkbuildid");

            Properties = new BuildProperties()
                .Add("branchurl", BranchUrl)
                .Add("domain", Domain)
                .Add("jira", Jira.ToString())
                .Add("notstartbuilds", notstartbuilds);
        }

        private static OperationType GetOperation(string value)
        {
            OperationType result;
            switch (value)
            {
                case "unit": result = OperationType.Unit; break;
                case "smoke": result = OperationType.Smoke; break;
                case "build": result = OperationType.Build; break;
                default :
                {
                    throw new ArgumentOutOfRangeException("Неизвестный тип операции: " + value);
                }
            }

            return result;
        }

        private static string ClearFeature(string origName)
        {
            if (string.IsNullOrEmpty(origName))
                return null;

            origName = origName.ToLower();
            var arr = origName.Split(new[] { '.' });
            for (var i = 0; i <= arr.Length - 1; i++)
            {
                if (BranchRegex.IsMatch(arr[i]))
                {
                    var devexists = arr[i].StartsWith(@"dev-");
                    var staticexists = arr[i].StartsWith(@"static-");

                    arr[i] = BranchRegex.Match(arr[i]).Value;

                    if (devexists)
                        arr[i] = string.Concat(@"dev-", arr[i]);

                    if (staticexists)
                        arr[i] = string.Concat(@"static-", arr[i]);
                }

            }
            return string.Join(@".", arr).Replace("feature/", "");
        }
    }

    public enum OperationType
    {
        Build = 1,
        Unit = 2,
        Smoke = 3
    }
}
