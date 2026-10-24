param([string]$Endpoint = 'http://localhost:8000')
$target = $null
if (-not [Uri]::TryCreate($Endpoint, [UriKind]::Absolute, [ref]$target) -or
    $target.Scheme -notin @('http', 'https') -or -not $target.IsLoopback -or
    $target.UserInfo -or $target.Query -or $target.Fragment) {
    throw 'This helper provisions DynamoDB Local only; provide a loopback HTTP(S) endpoint without credentials, query, or fragment.'
}
$previousKey = $env:AWS_ACCESS_KEY_ID
$previousSecret = $env:AWS_SECRET_ACCESS_KEY
$previousToken = $env:AWS_SESSION_TOKEN
try {
    $env:AWS_ACCESS_KEY_ID = 'fakeMyKeyId'
    $env:AWS_SECRET_ACCESS_KEY = 'fakeSecretAccessKey'
    $env:AWS_SESSION_TOKEN = $null
    foreach ($table in @('AttributionSnapshots', 'Purchases', 'PurchaseCustomers', 'AICreditLedger', 'AICreditLedgerTest')) {
    aws dynamodb describe-table --table-name $table --endpoint-url $Endpoint --region us-east-1 --query Table.TableName 2>$null
    if ($LASTEXITCODE -ne 0) {
        aws dynamodb create-table --table-name $table --attribute-definitions AttributeName=Id,AttributeType=S --key-schema AttributeName=Id,KeyType=HASH --billing-mode PAY_PER_REQUEST --endpoint-url $Endpoint --region us-east-1 --query TableDescription.TableName
        if ($LASTEXITCODE -ne 0) { throw "Could not create $table" }
    }
    }
}
finally {
    $env:AWS_ACCESS_KEY_ID = $previousKey
    $env:AWS_SECRET_ACCESS_KEY = $previousSecret
    $env:AWS_SESSION_TOKEN = $previousToken
}
