﻿using System.Reflection;
using System.Web;
using System.Web.Mvc;
using System.Web.Optimization;
using System.Web.Routing;
using Abp.Localization;
using Abp.Localization.Dictionaries;
using Abp.Localization.Dictionaries.Xml;
using Abp.Modules;
using Abp.Web.Mvc;

namespace SimpleTaskSystem.WebSpaDurandal
{
    [DependsOn(
        typeof(SimpleTaskSystemDataModule), 
        typeof(SimpleTaskSystemWebApiModule), 
        typeof(AbpWebMvcModule)
        )]
    public class SimpleTaskSystemWebModule : AbpModule
    {
        public override void PreInitialize()
        {
            //Add/remove languages for your application
            Configuration.Localization.Languages.Add(new LanguageInfo("en", "English", "famfamfam-flag-england", true));
            Configuration.Localization.Languages.Add(new LanguageInfo("tr", "Türkçe", "famfamfam-flag-tr"));
            Configuration.Localization.Languages.Add(new LanguageInfo("zh-CN", "简体中文", "famfamfam-flag-cn"));

            //Add a localization source
            Configuration.Localization.Sources.Add(
                new DictionaryBasedLocalizationSource(
                    "SimpleTaskSystem",
                    new XmlFileLocalizationDictionaryProvider(
                        HttpContext.Current.Server.MapPath("~/Localization/SimpleTaskSystem")
                        )
                    )
                );

            //Configure navigation/menu
            Configuration.Navigation.Providers.Add<SimpleTaskSystemNavigationProvider>();
        }

        public override void Initialize()
        {
            IocManager.RegisterAssemblyByConvention(Assembly.GetExecutingAssembly());

            AreaRegistration.RegisterAllAreas();
            RouteConfig.RegisterRoutes(RouteTable.Routes);
            BundleConfig.RegisterBundles(BundleTable.Bundles);
        }
    }
}
