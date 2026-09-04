using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Pulumi;
using Pulumi.Aws;
using Pulumi.Aws.Acm;
using Pulumi.Aws.CloudFront;
using Pulumi.Aws.CloudFront.Inputs;
using Pulumi.Aws.Iam;
using Pulumi.Aws.Iam.Inputs;
using Pulumi.Aws.Inputs;
using Pulumi.Aws.Route53;
using Pulumi.Aws.S3;
using Config = Pulumi.Config;

// ReSharper disable UnusedVariable

return await Deployment.RunAsync(() =>
{
    var config = new Config();

    var prefix = $"{Deployment.Instance.ProjectName}-{Deployment.Instance.StackName}";
    var domain = config.Require("domain");
    var viewerRequestFunctionFile = config.Require("viewer-request-function-file");
    var awsAccountId = config.Require("aws-account-id");
    var awsIacRoleArn = config.Require("aws-iac-role-arn");
    var awsZoneId = config.Require("aws-zone-id");

    var provider = new Provider($"{prefix}-provider", new ProviderArgs
    {
        AllowedAccountIds = [ awsAccountId ],
        AssumeRoles = new ProviderAssumeRoleArgs
        {
            RoleArn = awsIacRoleArn,
            SessionName = "pulumi-deploy"
        },
        Region = "us-east-1"
    });

    var count = 1;
    var certificate = new Certificate($"{prefix}-certicate", new CertificateArgs
    {
        DomainName = domain,
        ValidationMethod = "DNS"
    }, new CustomResourceOptions { Provider = provider });

    var validationRecordFqdns = certificate.DomainValidationOptions.Apply(domainValidationOptions =>
    {
        List<Record> validationRecords = [];
        foreach (var option in domainValidationOptions)
        {
            if (option.DomainName is null
                || option.ResourceRecordName is null
                || option.ResourceRecordType is null
                || option.ResourceRecordValue is null)
            {
                continue;
            }

            validationRecords.Add(new Record($"{prefix}-record-validation-{count:00}", new RecordArgs
            {
                AllowOverwrite = true,
                Name = option.ResourceRecordName,
                Records = [ option.ResourceRecordValue ],
                Ttl = 60,
                Type = option.ResourceRecordType,
                ZoneId = awsZoneId
            }, new CustomResourceOptions { Provider = provider }));
            count++;
        }

        return Output.All(validationRecords.Select(y => y.Fqdn));
    });

    var certificateValidation = new CertificateValidation($"{prefix}-certificatevalidation", new CertificateValidationArgs
    {
        CertificateArn = certificate.Arn,
        ValidationRecordFqdns = validationRecordFqdns,
    }, new CustomResourceOptions { Provider = provider });

    var bucket = new Bucket($"{prefix}-bucket", new BucketArgs
    {
        ForceDestroy = true
    }, new CustomResourceOptions { Provider = provider });

    var originAccessControl = new OriginAccessControl($"{prefix}-originaccesscontrol", new OriginAccessControlArgs
    {
        OriginAccessControlOriginType = "s3",
        SigningBehavior = "always",
        SigningProtocol = "sigv4"
    }, new CustomResourceOptions { Provider = provider });

    var originId = $"{prefix}-origin";

    var viewerRequestFunction = new Function($"{prefix}-function-viewerrequest", new FunctionArgs
    {
        Code = File.ReadAllText(viewerRequestFunctionFile),
        Runtime = "cloudfront-js-2.0"
    }, new CustomResourceOptions { Provider = provider });

    var distribution = new Distribution($"{prefix}-distribution", new DistributionArgs
    {
        Aliases = [ domain ],
        CustomErrorResponses =
        [
            new DistributionCustomErrorResponseArgs
            {
                ErrorCode = 403,
                ResponseCode = 404,
                ResponsePagePath = "/index.html"
            }
        ],
        DefaultRootObject = "index.html",
        DefaultCacheBehavior = new DistributionDefaultCacheBehaviorArgs
        {
            AllowedMethods = ["GET", "HEAD"],
            CachePolicyId = "658327ea-f89d-4fab-a63d-7e88639e58f6",
            CachedMethods = ["GET", "HEAD"],
            Compress = true,
            TargetOriginId = originId,
            ViewerProtocolPolicy = "redirect-to-https",
            FunctionAssociations =
            [
                new DistributionDefaultCacheBehaviorFunctionAssociationArgs
                {
                    EventType = "viewer-request",
                    FunctionArn = viewerRequestFunction.Arn
                },
            ]
        },
        Enabled = true,
        HttpVersion = "http2and3",
        Origins = new[]
        {
            new DistributionOriginArgs
            {
                DomainName = bucket.BucketRegionalDomainName,
                OriginAccessControlId = originAccessControl.Id,
                OriginId = originId,
            }
        },
        PriceClass = "PriceClass_100",
        Restrictions = new DistributionRestrictionsArgs
        {
            GeoRestriction = new DistributionRestrictionsGeoRestrictionArgs
            {
                Locations = [],
                RestrictionType = "none"
            }
        },
        RetainOnDelete = false,
        ViewerCertificate = new DistributionViewerCertificateArgs
        {
            AcmCertificateArn = certificate.Arn,
            SslSupportMethod = "sni-only",
            MinimumProtocolVersion = "TLSv1.2_2021"
        },
        WaitForDeployment = false,
    }, new CustomResourceOptions { Provider = provider, DependsOn = certificateValidation });

    var bucketPolicy = new BucketPolicy($"{prefix}-bucketpolicy", new BucketPolicyArgs
    {
        Bucket = bucket.BucketName,
        Policy = GetPolicyDocument.Invoke(new GetPolicyDocumentInvokeArgs
        {
            Version = "2012-10-17",
            Statements =
            [
                new GetPolicyDocumentStatementInputArgs
                {
                    Effect = "Allow",
                    Principals =
                    [
                        new GetPolicyDocumentStatementPrincipalInputArgs
                        {
                            Identifiers = ["cloudfront.amazonaws.com"],
                            Type = "Service"
                        }
                    ],
                    Actions = ["s3:GetObject"],
                    Resources = [ bucket.Arn.Apply(x => $"{x}/*") ],
                    Conditions =
                    [
                        new GetPolicyDocumentStatementConditionInputArgs
                        {
                            Test = "StringEquals",
                            Values = distribution.Arn,
                            Variable = "AWS:SourceArn"
                        }
                    ],
                }
            ]
        }, new InvokeOptions{ Provider = provider}).Apply(x => x.Json)
    }, new CustomResourceOptions { Provider = provider });

    var record = new Record($"{prefix}-record", new RecordArgs
    {
        Name = "hugoblog",
        Ttl = 300,
        Type = "CNAME",
        Records = [ distribution.DomainName ],
        ZoneId = awsZoneId
    }, new CustomResourceOptions { Provider = provider });

    return new Dictionary<string, object?>
    {
        ["bucket-name"] = bucket.BucketName,
        ["distribution-id"] = distribution.Id,
        ["version"] = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? "1.0.0",
    };
});
