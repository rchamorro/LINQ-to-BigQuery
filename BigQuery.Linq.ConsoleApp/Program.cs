﻿using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Bigquery.v2;
using Google.Apis.Bigquery.v2.Data;
using Google.Apis.Services;
using Google.Apis.Util;
using Google.Apis.Util.Store;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BigQuery.Linq.ConsoleApp
{



    class Program
    {


        static void Main(string[] args)
        {
       
        }
    }




    [TableName("publicdata:samples.trigrams")]
    public class trigrams
    {
        public cell cell { get; set; }
    }

    public class cell
    {
        public string value { get; set; }
    }
}