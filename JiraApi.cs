using System;
using System.Collections.Generic;
using System.Configuration;
using System.Linq;
using TechTalk.JiraRestClient;

namespace JiraWithTC
{
    public class JiraApi
    {
        /// <summary>
        /// Клиент запросов к жире
        /// </summary>
        private readonly JiraClient<CustomIssueFields> _jiraClient;

        private readonly JiraInstanceElement _jiraInfo;

        /// <summary>
        /// расширение стандарного объекта Issue для обработки поля Branch (cf[11104])
        /// </summary>
        public class CustomIssueFields : IssueFields
        {
            public string Customfield_11104 { get; set; }
        }

        public JiraApi(string name)
        {
            var jiraDefaults = ConfigurationManager.GetSection("JiraDefaults") as JiraDefaults;

            if (jiraDefaults == null)
                throw new ConfigurationErrorsException("Не найдены настройки для TC");

            _jiraInfo = jiraDefaults.Instances[name];

            //клиент жиры апи
            _jiraClient = new JiraClient<CustomIssueFields>(_jiraInfo.Url, _jiraInfo.User, _jiraInfo.Pass);
        }

        /// <summary>
        /// Обновить статусы у задач в жире
        /// </summary>
        /// <param name="branchName">название бранча</param>
        /// <param name="branchUrl">урл среды</param>
        /// <param name="failMessage">сообщение падения</param>
        /// <param name="url">урл тимсити</param>
        /// <param name="buildId">номер билда</param>
        public void UpdateJiraStatus(string branchName, string branchUrl, string failMessage, string url, string buildId)
        {
            //список задач ожидающихся в тест
            var issueListTest =
                _jiraClient.GetIssuesByQuery(_jiraInfo.ProjectKey, null,
                    "status = \"" + _jiraInfo.TransitionTestName + "\" and resolution = Fixed and cf[11104] = \"" + branchName +
                    "\"").ToList();

            //список задач ожидающихся в релиз
            var issueListRelease =
                _jiraClient.GetIssuesByQuery(_jiraInfo.ProjectKey, null,
                    "status = \"" + _jiraInfo.TransitionReleaseName + "\" and resolution = Fixed and cf[11104] = \"" +
                    branchName + "\"").ToList();

            if (failMessage == null)
            {
                //переводим в тест
                var tranTest = new Transition
                {
                    id = _jiraInfo.TransitionTestId,
                    fields = new {environment = branchUrl}
                };
                foreach (var issue in issueListTest)
                {
                    _jiraClient.TransitionIssue(issue, tranTest);
                    Console.WriteLine("Задача " + issue.key + " переведена в тест");
                }
                //переводим в релиз
                var tranRelease = new Transition
                {
                    id = _jiraInfo.TransitionReleaseId,
                    fields = new {environment = branchUrl}
                };
                foreach (var issue in issueListRelease)
                {
                    _jiraClient.TransitionIssue(issue, tranRelease);
                    Console.WriteLine("Задача " + issue.key + " переведена в релиз");
                }
            }
            //билд упал - переводим задачи Fixed в develop
            else
            {
                //переводим в develop
                var tranTest = new Transition
                {
                    id = _jiraInfo.TransitionFailedTestId
                };

                foreach (var issue in issueListTest)
                {
                    _jiraClient.TransitionIssue(issue, tranTest);
                    _jiraClient.CreateComment(issue, "Билд упал. Cообщение об ошибке: " + failMessage + Environment.NewLine +
                        url + "/viewLog.html?buildId=" + buildId);
                    Console.WriteLine("Задача " + issue.key + " переведена в develop");
                }

                //переводим в develop
                var tranRelease = new Transition
                {
                    id = _jiraInfo.TransitionFailedReleaseId
                };

                //переводим в develop
                foreach (var issue in issueListRelease)
                {
                    _jiraClient.TransitionIssue(issue, tranRelease);
                    _jiraClient.CreateComment(issue, "Билд упал. Cообщение об ошибке: " + failMessage + Environment.NewLine +
                        url + "/viewLog.html?buildId=" + buildId);
                    Console.WriteLine("Задача " + issue.key + " переведена в Failed Build");
                }
            }

            //список задач ожидающихся в тест в резолюции не fixed
            var issueListTestNotFixed =
                _jiraClient.GetIssuesByQuery(_jiraInfo.ProjectKey, null,
                    "status = \"" + _jiraInfo.TransitionTestName + "\" and resolution != Fixed").ToList();

            //список задач ожидающихся в релиз в резолюции не fixed
            var issueListReleaseNotFixed =
                _jiraClient.GetIssuesByQuery(_jiraInfo.ProjectKey, null,
                    "status = \"" + _jiraInfo.TransitionReleaseName + "\" and resolution != Fixed").ToList();

            //Задачи которые в резолюции не фиксед
            //по умолчанию переводим в тест и в релиз без указания урла
            //так как для них скорей всего не создавали бранча
            var tranTestNotFixed = new Transition
            {
                id = _jiraInfo.TransitionTestId,
                fields = new { environment = _jiraInfo.UrlByDefault }
            };
            foreach (var issue in issueListTestNotFixed)
            {
                _jiraClient.TransitionIssue(issue, tranTestNotFixed);
                Console.WriteLine("Задача " + issue.key + " переведена в тест");
            }

            var tranReleaseNotFixed = new Transition
            {
                id = _jiraInfo.TransitionReleaseId,
                fields = new { environment = _jiraInfo.UrlByDefault }
            };
            foreach (var issue in issueListReleaseNotFixed)
            {
                _jiraClient.TransitionIssue(issue, tranReleaseNotFixed);
                Console.WriteLine("Задача " + issue.key + " переведена в релиз");
            }

            //вывод
            Console.WriteLine(
                "Перевод задач для бранча " + branchName + " (" + branchUrl + ") :" + Environment.NewLine +
                issueListTest.Count + " задач было переведено " + (failMessage != null ? "в develop" : "в тест - Fixed") + Environment.NewLine +
                issueListTestNotFixed.Count + " задач было переведено в тест - не Fixed" + Environment.NewLine +
                issueListRelease.Count + " задач было переведено " + (failMessage != null ? "в develop" : "в релиз - Fixed") + Environment.NewLine +
                issueListReleaseNotFixed.Count + " задач было переведено в релиз - не Fixed");
        }

        /// <summary>
        /// Получить список бранчей, которые ожидаются в билд по задачам
        /// </summary>
        /// <returns></returns>
        public List<string> GetWaitIssues()
        {
            //список задач ожидающихся в тест
            var issueListTest = _jiraClient.GetIssuesByQuery(_jiraInfo.ProjectKey, null, "status = \"" + _jiraInfo.TransitionTestName + "\"").ToList();
            //список задач ожидающихся в релиз
            var issueListRelease = _jiraClient.GetIssuesByQuery(_jiraInfo.ProjectKey, null, "status = \"" + _jiraInfo.TransitionReleaseName + "\"").ToList();

            var branchList = new List<string>();

            foreach (var issue in issueListTest)
            {
                if (!branchList.Contains(issue.fields.Customfield_11104))
                    branchList.Add(issue.fields.Customfield_11104);
            }

            foreach (var issue in issueListRelease)
            {
                if (!branchList.Contains(issue.fields.Customfield_11104))
                    branchList.Add(issue.fields.Customfield_11104);
            }

            return branchList;
        }
    }

    /// <summary>
    /// Настройки для конфигураций для ТС
    /// </summary>

    public class JiraDefaults : ConfigurationSection
    {
        [ConfigurationProperty("", IsRequired = true, IsDefaultCollection = true)]
        public JiraInstanceCollection Instances
        {
            get { return (JiraInstanceCollection)this[""]; }
            set { this[""] = value; }
        }
    }

    public class JiraInstanceCollection : ConfigurationElementCollection
    {
        protected override ConfigurationElement CreateNewElement()
        {
            return new JiraInstanceElement();
        }

        protected override object GetElementKey(ConfigurationElement element)
        {
            return ((JiraInstanceElement)element).Name;
        }

        public new JiraInstanceElement this[string elementName]
        {
            get
            {
                var collection = this.OfType<JiraInstanceElement>().ToList();

                if (collection.All(t => t.Name != elementName))
                    throw new ArgumentOutOfRangeException(elementName);

                return collection.FirstOrDefault(item => item.Name == elementName);
            }
        }
    }

    public class JiraInstanceElement : ConfigurationElement
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
        /// адрес жиры
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
        /// юзер который переводит задачи
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
        /// пароль юзера
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
        /// ключ-название проекта
        /// </summary>
        [ConfigurationProperty("projectKey", IsKey = true, IsRequired = true)]
        public String ProjectKey
        {
            get
            {
                return (String)base["projectKey"];
            }
            set
            {
                base["projectKey"] = value;
            }
        }

        /// <summary>
        /// номер "кнопки" перевода в тест
        /// </summary>
        [ConfigurationProperty("transitionTestId", IsKey = true, IsRequired = true)]
        public String TransitionTestId
        {
            get
            {
                return (String)base["transitionTestId"];
            }
            set
            {
                base["transitionTestId"] = value;
            }
        }

        /// <summary>
        /// номер "кнопки" перевода в релиз
        /// </summary>
        [ConfigurationProperty("transitionReleaseId", IsKey = true, IsRequired = true)]
        public String TransitionReleaseId
        {
            get
            {
                return (String)base["transitionReleaseId"];
            }
            set
            {
                base["transitionReleaseId"] = value;
            }
        }

        /// <summary>
        /// номер "кнопки" перевода в develop
        /// </summary>
        [ConfigurationProperty("transitionFailedTestId", IsKey = true, IsRequired = true)]
        public String TransitionFailedTestId
        {
            get
            {
                return (String)base["transitionFailedTestId"];
            }
            set
            {
                base["transitionFailedTestId"] = value;
            }
        }

        /// <summary>
        /// номер "кнопки" перевода в develop
        /// </summary>
        [ConfigurationProperty("transitionFailedReleaseId", IsKey = true, IsRequired = true)]
        public String TransitionFailedReleaseId
        {
            get
            {
                return (String)base["transitionFailedReleaseId"];
            }
            set
            {
                base["transitionFailedReleaseId"] = value;
            }
        }

        /// <summary>
        /// название статуса ожидания в теста
        /// </summary>
        [ConfigurationProperty("transitionTestName", IsKey = true, IsRequired = true)]
        public String TransitionTestName
        {
            get
            {
                return (String)base["transitionTestName"];
            }
            set
            {
                base["transitionTestName"] = value;
            }
        }

        /// <summary>
        /// название статуса ожидания в релиз
        /// </summary>
        [ConfigurationProperty("transitionReleaseName", IsKey = true, IsRequired = true)]
        public String TransitionReleaseName
        {
            get
            {
                return (String)base["transitionReleaseName"];
            }
            set
            {
                base["transitionReleaseName"] = value;
            }
        }

        /// <summary>
        /// Урл на который указывать по умолчанию в "environment"
        /// </summary>
        [ConfigurationProperty("urlByDefault", IsKey = true, IsRequired = true)]
        public String UrlByDefault
        {
            get
            {
                return (String)base["urlByDefault"];
            }
            set
            {
                base["urlByDefault"] = value;
            }
        }
    }
}
