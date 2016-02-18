using System;
using System.Collections.Generic;
using System.Configuration;
using System.Linq;
using System.Net;
using System.Text;
using RestSharp;
using RestSharp.Deserializers;

namespace JiraWithTC
{
    public class TeamCityApi
    {
        /// <summary>
        /// Десериализатор для ответов от тимсити
        /// </summary>
        private static readonly JsonDeserializer Deserializer = new JsonDeserializer();

        private readonly TeamCityInstanceElement _teamCity;

        public TeamCityApi(string name)
        {
            var teamCityDefaults = ConfigurationManager.GetSection("TeamCityDefaults") as TeamCityDefaults;

            if (teamCityDefaults == null)
                throw new ConfigurationErrorsException("Не найдены настройки для TC");

            _teamCity = teamCityDefaults.Instances[name];
        }

        public string UrlToSend { get { return _teamCity.UrlToSend; } }

        /// <summary>
        /// Создать тело запроса
        /// </summary>
        /// <param name="method">тип запрос</param>
        /// <param name="path">доп. урл</param>
        /// <returns></returns>
        private RestRequest CreateRequest(Method method, String path)
        {
            var request = new RestRequest { Method = method, Resource = path, RequestFormat = DataFormat.Json };
            request.AddHeader("Authorization", "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(String.Format("{0}:{1}", _teamCity.User, _teamCity.Pass))));
            return request;
        }

        /// <summary>
        /// Выполнить запрос
        /// </summary>
        /// <param name="request"></param>
        /// <returns></returns>
        private IRestResponse ExecuteRequest(IRestRequest request)
        {
            var client = new RestClient(_teamCity.Url);
            var response = client.Execute(request);
            AssertStatus(response);
            return response;
        }

        /// <summary>
        /// Проверить статус ответа
        /// </summary>
        /// <param name="response">ответ</param>
        private static void AssertStatus(IRestResponse response)
        {
            if (response.ErrorException != null)
                throw new Exception(string.Format("Transport level error: {0}{1}", response.ErrorMessage, Environment.NewLine));
            if (response.StatusCode != HttpStatusCode.OK)
                throw new Exception(string.Format("TeamCity returned wrong status: {0}{1}{2}{1}", response.StatusDescription, Environment.NewLine, response.Content));
        }

        /// <summary>
        /// Запустить билды
        /// </summary>
        /// <param name="branchList">список бранчей для запуска</param>
        /// <param name="param">параметры билда</param>
        public void StartBuilds(List<string> branchList, Parameters param)
        {
            //убираем из списка бранчи, которые уже запущены
            RemoveStartedAndWaitedBuilds("/httpAuth/app/rest/builds/?locator=running:true,branch:default:any", branchList);
            //убираем из списка бранчи, которые уже в очереди на запуск
            RemoveStartedAndWaitedBuilds("/httpAuth/app/rest/buildQueue", branchList);
            //убираем из списка бранчи, у которые уже были сбилжены и у которых нет изменени
            RemoveBuildsWithOutChanges(param.BuildType, branchList);

            foreach (var branchName in branchList)
            {
                if (string.IsNullOrEmpty(_teamCity.BranchNameToIgnore) || 
                    _teamCity.BranchNameToIgnore.Split(',').Select(t => t.Trim()).All(k => !branchName.StartsWith(k)))
                    AddBuildToQueue(branchName, param);  
            }
        }

        /// <summary>
        /// Проверка статуса билда
        /// </summary>
        /// <param name="buildId"></param>
        /// <returns></returns>
        public string CheckStatus(string buildId)
        {
            var request = CreateRequest(Method.GET, "/httpAuth/app/rest/builds/id:" + buildId);
            var response = ExecuteRequest(request);

            var build = Deserializer.Deserialize<BuildInfo>(response);

            return build.Status != StatusType.Success ? build.StatusText : null;
        }

        /// <summary>
        /// Смотрим есть ли "несбидженные" изменения
        /// </summary>
        /// <param name="buildType">айди конфиругации билда</param>
        /// <param name="branch">название бранча</param>
        /// <returns></returns>
        private bool IsChangesExist(string buildType, string branch)
        {
            //сначала вытаскиваем все последние билды этого бранча на этой конфигурации
            var request = CreateRequest(Method.GET, "/httpAuth/app/rest/builds/?locator=branch:name:" + branch + ",buildType:" + buildType);
            var response = ExecuteRequest(request);

            var builds = Deserializer.Deserialize<BuildCollection>(response);

            //если успешных билдов нет - "несбилженные" изменения есть (тупо нада сбилдить в первый раз нормально)
            if (builds.Build.Any(t => t.Status == StatusType.Success))
            {
                var build = builds.Build.Where(k => k.Status == StatusType.Success).OrderByDescending(t => t.Id).First();

                //вытаскиваем последний успешный билд
                request = CreateRequest(Method.GET, "/httpAuth/app/rest/builds/id:" + build.Id);
                response = ExecuteRequest(request);

                var buildInfo = Deserializer.Deserialize<BuildInfo>(response);

                var isLastChange = false;
                var changeId = 0;

                //вытаскиваем последнее "изменение"
                if (buildInfo.LastChanges.Change.Any())
                {
                    changeId = buildInfo.LastChanges.Change.First().Id;
                    isLastChange = true;
                }

                //фильтруем список изменений для данного бранча и типа билда относительно последнего успешно сбидженного изменения
                request = CreateRequest(Method.GET, "/httpAuth/app/rest/changes/?locator=branch:name:" + branch + ",buildType:" + buildType + (isLastChange ? "&sinceChange=id:" + changeId : ""));
                response = ExecuteRequest(request);

                var changes = Deserializer.Deserialize<ChangeCollection>(response);

                //если список не пустой - изменения есть - нужно сбилдить
                return changes.Change.Any();
            }

            return true;
        }

        private void RemoveBuildsWithOutChanges(string buildType, ICollection<string> branchList)
        {
            var removeList = branchList.Where(branch => !IsChangesExist(buildType, branch)).ToList();

            foreach (var remove in removeList)
                branchList.Remove(remove);
        }

        /// <summary>
        /// Убрать из списка бранчей значения содержащиеся в ответе запроса url
        /// и которые имеют определенный тип билда
        /// </summary>
        /// <param name="url">урл</param>
        /// <param name="branchList">список бранчей</param>
        private void RemoveStartedAndWaitedBuilds(string url, ICollection<string> branchList)
        {
            var request = CreateRequest(Method.GET, url);
            var response = ExecuteRequest(request);

            var builds = Deserializer.Deserialize<BuildCollection>(response);

            //список типов билдов для которых работает фильтрация
            var buildTypeList = _teamCity.BuildIdToWatch.Split(',').Select(t => t.Trim()).ToList();

            foreach (var build in builds.Build)
            {
                if (branchList.Contains(build.BranchName) && (!buildTypeList.Any() || buildTypeList.Contains(build.BuildTypeId)))
                {
                    branchList.Remove(build.BranchName);
                    Console.WriteLine("Бранч " + build.BranchName + " уже был запущен на публикацию в " + build.BuildTypeId);
                }
            }
        }

        /// <summary>
        /// Добавить бранч в очередь на билдование
        /// </summary>
        /// <param name="branchName">имя бранча</param>
        /// <param name="param">параметры билда</param>
        private void AddBuildToQueue(string branchName, Parameters param)
        {
            var request = CreateRequest(Method.POST, "/httpAuth/app/rest/buildQueue");
            request.AddHeader("ContentType", "application/json");

            var build = new
            {
                buildType = new { id = param.BuildType },
                branchName,
                properties = new
                {
                    count = param.Properties.GetCount(),
                    property = param.Properties.GetObject()
                }
            };

            request.AddBody(build);
            ExecuteRequest(request);
            Console.WriteLine("Бранч " + branchName + " добавлен в очередь");
        }
    }

    public class BuildInfo
    {
        public int Id { get; set; }
        public string BuildTypeId { get; set; }
        public int Number { get; set; }
        public StatusType Status { get; set; }
        public string State { get; set; }
        public string BranchName { get; set; }
        public bool DefaultBranch { get; set; }
        public int PercentageComplete { get; set; }
        public string StatusText { get; set; }
        public ChangeCollection LastChanges { get; set; }
    }

    public class Change
    {
        public int Id { get; set; }
    }


    public class BuildProperties
    {
        private readonly Dictionary<string, string> _parameters = new Dictionary<string, string>();

        public BuildProperties Add(string key, string value)
        {
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(value))
                return this;

            _parameters.Add(key, value);
            return this;
        }

        public object GetObject()
        {
            var list = new List<object>();

            foreach (var parameter in _parameters)
            {
                list.Add(new { name = "env.jwt." + parameter.Key, value = parameter.Value });
            }

            return list;
        }

        public int GetCount()
        {
            return _parameters.Count;
        }
    }

    public enum StatusType
    {
        Failure,
        Success,
        Error
    }

    public class BuildCollection
    {
        public BuildCollection()
        {
            Build = new List<BuildInfo>();
        }

        public List<BuildInfo> Build { get; set; }
    }

    public class ChangeCollection
    {
        public ChangeCollection()
        {
            Change = new List<Change>();
        }

        public List<Change> Change { get; set; }
    }

    /// <summary>
    /// Настройки для конфигураций для ТС
    /// </summary>

    public class TeamCityDefaults : ConfigurationSection
    {
        [ConfigurationProperty("", IsRequired = true, IsDefaultCollection = true)]
        public TeamCityInstanceCollection Instances
        {
            get { return (TeamCityInstanceCollection)this[""]; }
            set { this[""] = value; }
        }
    }

    public class TeamCityInstanceCollection : ConfigurationElementCollection
    {
        protected override ConfigurationElement CreateNewElement()
        {
            return new TeamCityInstanceElement();
        }

        protected override object GetElementKey(ConfigurationElement element)
        {
            return ((TeamCityInstanceElement)element).Name;
        }

        public new TeamCityInstanceElement this[string elementName]
        {
            get
            {
                var collection = this.OfType<TeamCityInstanceElement>().ToList();

                if (collection.All(t => t.Name != elementName))
                    throw new ArgumentOutOfRangeException(elementName);

                return collection.FirstOrDefault(item => item.Name == elementName);
            }
        }
    }

    public class TeamCityInstanceElement : ConfigurationElement
    {
        [ConfigurationProperty("name", IsKey = true, IsRequired = true)]
        public String Name
        {
            get
            {
                return (String)base["name"];
            }
            set
            {
                base["name"] = value;
            }
        }

        /// <summary>
        /// Адрес тимсити
        /// </summary>
        [ConfigurationProperty("url", IsKey = true, IsRequired = true)]
        public String Url
        {
            get
            {
                return (String)base["url"];
            }
            set
            {
                base["url"] = value;
            }
        }

        /// <summary>
        /// Юзер в тимсити
        /// </summary>
        [ConfigurationProperty("user", IsKey = true, IsRequired = true)]
        public String User
        {
            get
            {
                return (String)base["user"];
            }
            set
            {
                base["user"] = value;
            }
        }

        /// <summary>
        /// Пароль юзера в тимсити
        /// </summary>
        [ConfigurationProperty("pass", IsKey = true, IsRequired = true)]
        public String Pass
        {
            get
            {
                return (String)base["pass"];
            }
            set
            {
                base["pass"] = value;
            }
        }

        /// <summary>
        /// Отображаемый урл для тимсити (может не совпадать с фактическим)
        /// </summary>
        [ConfigurationProperty("urlToSend", IsKey = true, IsRequired = true)]
        public String UrlToSend
        {
            get
            {
                return (String)base["urlToSend"];
            }
            set
            {
                base["urlToSend"] = value;
            }
        }

        /// <summary>
        /// Айди билда в ТС который запускает публикацию дев сред
        /// </summary>
        [ConfigurationProperty("devBuildId", IsKey = true)]
        public String DevBuildId
        {
            get
            {
                return (String)base["devBuildId"];
            }
            set
            {
                base["devBuildId"] = value;
            }
        }

        /// <summary>
        /// Айди билдов для которых будет проверка очереди перед запуском
        /// чтобы не запускать уже запущенные ветки
        /// </summary>
        [ConfigurationProperty("buildIdToWatch", IsKey = true)]
        public String BuildIdToWatch
        {
            get
            {
                return (String)base["buildIdToWatch"];
            }
            set
            {
                base["buildIdToWatch"] = value;
            }
        }

        /// <summary>
        /// Начало названия бранчей, для которых не делать билда
        /// </summary>
        [ConfigurationProperty("branchNameToIgnore", IsKey = true)]
        public String BranchNameToIgnore
        {
            get
            {
                return (String)base["branchNameToIgnore"];
            }
            set
            {
                base["branchNameToIgnore"] = value;
            }
        }
    }
}
