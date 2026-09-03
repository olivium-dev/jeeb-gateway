def env_key: split("=")[0] | ascii_downcase | gsub(":"; "__");
def is_devtool_flag:
  env_key as $key
  | [
      "superlogin__openmode",
      "demousers__enabled",
      "features__devendpoints__enabled",
      "features__swagger__enabled",
      "jeebfirebasecontract__schemaversion",
      "jeebfirebasecontract__projectid",
      "jeebfirebasecontract__projectnumber",
      "jeebfirebasecontract__firestoredatabaseid",
      "jeebfirebasecontract__chatenabled",
      "jeebfirebasecontract__pushproducer",
      "firebase__chat__projectid",
      "firebase__chat__serviceaccountkeypath",
      "featureflags__notificationdurablewrite__enabled",
      "featureflags__notificationoutboxmode",
      "featureflags__pushdispatchmode",
      "firestore__databaseid",
      "firebase__firestoredatabaseid",
      "firebase__chat__firestoredatabaseid"
    ]
  | index($key) != null;
def patch_devtool($document):
  $document
  | .TaskTemplate.ContainerSpec.Image = $image
  | .TaskTemplate.ContainerSpec.Env = (
      ((.TaskTemplate.ContainerSpec.Env // [])
        | map(select(is_devtool_flag | not)))
      + [
          "SuperLogin__OpenMode=true",
          "DemoUsers__Enabled=true",
          "Features__DevEndpoints__Enabled=true",
          "Features__Swagger__Enabled=true",
          "JeebFirebaseContract__SchemaVersion=1",
          "JeebFirebaseContract__ProjectId=jeeb-5a293",
          "JeebFirebaseContract__ProjectNumber=1051234312170",
          "JeebFirebaseContract__FirestoreDatabaseId=(default)",
          "JeebFirebaseContract__ChatEnabled=true",
          "JeebFirebaseContract__PushProducer=notification-service",
          "Firebase__Chat__ProjectId=jeeb-5a293",
          "Firebase__Chat__ServiceAccountKeyPath=/run/secrets/firebase_admin_json",
          "FeatureFlags__NotificationDurableWrite__Enabled=true",
          "FeatureFlags__NotificationOutboxMode=upstream-authority",
          "FeatureFlags__PushDispatchMode=local"
        ]
    )
  | .TaskTemplate.ContainerSpec.Secrets = (
      ((.TaskTemplate.ContainerSpec.Secrets // [])
        | map(select(.File.Name != "firebase_admin_json")))
      + $firebase_secret[0]
    )
  | .UpdateConfig = ((.UpdateConfig // {}) + {
      Parallelism:1,
      Monitor:20000000000,
      FailureAction:"pause",
      Order:"start-first"
    })
  | .RollbackConfig = ((.RollbackConfig // {}) + {
      Parallelism:1,
      Monitor:20000000000,
      FailureAction:"pause",
      Order:"start-first"
    });
def incumbent_devtool_keys:
  [
    ($incumbent[0].TaskTemplate.ContainerSpec.Env // [])[]
    | select(is_devtool_flag)
    | env_key
  ];

($incumbent | length) == 1
and ($firebase_secret | length) == 1
and ($firebase_secret[0] | length) == 1
and $firebase_secret[0][0].File.Name == "firebase_admin_json"
and $firebase_secret[0][0].File.UID == "65532"
and $firebase_secret[0][0].File.GID == "65532"
and $firebase_secret[0][0].File.Mode == 256
and ($firebase_secret[0][0].SecretID | test("^[a-z0-9]+$"))
and ($firebase_secret[0][0].SecretName
  | test("^jeeb_staging_fb_[A-Za-z0-9_-]{43}$"))
and (incumbent_devtool_keys | length) == (incumbent_devtool_keys | unique | length)
and . == patch_devtool($incumbent[0])
