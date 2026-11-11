// Alerts: who finds out, and how, when something goes wrong.
//
// Each alert answers one operational question and sends an email through one action group.
// They resolve themselves when the condition clears.

param tags object

@description('Address that receives alert emails. Empty = alerts are created but notify nobody.')
param alertEmail string

param serviceBusNamespaceId string
param queueName string
param apiId string

resource operators 'Microsoft.Insights/actionGroups@2023-01-01' = {
  name: 'ag-webhook-operators'
  location: 'global'
  tags: tags
  properties: {
    groupShortName: 'webhook-ops'
    enabled: true
    emailReceivers: empty(alertEmail)
      ? []
      : [
          {
            name: 'operator'
            emailAddress: alertEmail
            useCommonAlertSchema: true
          }
        ]
  }
}

// "Did an event fail for good?" A message reaches the dead-letter queue after five failed
// deliveries or when it can never succeed. Either way a person has to look.
// Replay with tools/DeadLetterReplay once the cause is fixed.
resource deadLetterAlert 'Microsoft.Insights/metricAlerts@2018-03-01' = {
  name: 'alert-webhook-dead-letters'
  location: 'global'
  tags: tags
  properties: {
    description: 'Messages are in the dead-letter queue. Inspect them, fix the cause, then replay with tools/DeadLetterReplay.'
    severity: 2
    enabled: true
    scopes: [serviceBusNamespaceId]
    evaluationFrequency: 'PT1M'
    windowSize: 'PT5M'
    autoMitigate: true
    criteria: {
      'odata.type': 'Microsoft.Azure.Monitor.SingleResourceMultipleMetricCriteria'
      allOf: [
        {
          criterionType: 'StaticThresholdCriterion'
          name: 'dead-lettered-messages'
          metricNamespace: 'Microsoft.ServiceBus/namespaces'
          metricName: 'DeadletteredMessages'
          dimensions: [
            {
              name: 'EntityName'
              operator: 'Include'
              values: [queueName]
            }
          ]
          operator: 'GreaterThan'
          threshold: 0
          timeAggregation: 'Maximum'
        }
      ]
    }
    actions: [
      {
        actionGroupId: operators.id
      }
    ]
  }
}

// "Is the worker keeping up?" A backlog that stays high means the worker is down, cannot
// scale, or the database is slow.
resource backlogAlert 'Microsoft.Insights/metricAlerts@2018-03-01' = {
  name: 'alert-webhook-queue-backlog'
  location: 'global'
  tags: tags
  properties: {
    description: 'More than 100 messages have been waiting in the queue for 15 minutes. Check that the worker is running and scaling.'
    severity: 3
    enabled: true
    scopes: [serviceBusNamespaceId]
    evaluationFrequency: 'PT5M'
    windowSize: 'PT15M'
    autoMitigate: true
    criteria: {
      'odata.type': 'Microsoft.Azure.Monitor.SingleResourceMultipleMetricCriteria'
      allOf: [
        {
          criterionType: 'StaticThresholdCriterion'
          name: 'active-messages'
          metricNamespace: 'Microsoft.ServiceBus/namespaces'
          metricName: 'ActiveMessages'
          dimensions: [
            {
              name: 'EntityName'
              operator: 'Include'
              values: [queueName]
            }
          ]
          operator: 'GreaterThan'
          threshold: 100
          timeAggregation: 'Minimum'
        }
      ]
    }
    actions: [
      {
        actionGroupId: operators.id
      }
    ]
  }
}

// "Is the API rejecting webhooks because of our own faults?" 5xx means the provider's request
// was fine and we failed, for example because the queue was unreachable.
resource apiErrorsAlert 'Microsoft.Insights/metricAlerts@2018-03-01' = {
  name: 'alert-webhook-api-5xx'
  location: 'global'
  tags: tags
  properties: {
    description: 'The API answered with 5xx. The webhook provider will retry, but find out why the API could not accept events.'
    severity: 2
    enabled: true
    scopes: [apiId]
    evaluationFrequency: 'PT1M'
    windowSize: 'PT5M'
    autoMitigate: true
    criteria: {
      'odata.type': 'Microsoft.Azure.Monitor.SingleResourceMultipleMetricCriteria'
      allOf: [
        {
          criterionType: 'StaticThresholdCriterion'
          name: 'server-errors'
          metricNamespace: 'Microsoft.App/containerApps'
          metricName: 'Requests'
          dimensions: [
            {
              name: 'statusCodeCategory'
              operator: 'Include'
              values: ['5xx']
            }
          ]
          operator: 'GreaterThan'
          threshold: 0
          timeAggregation: 'Total'
          // The metric appears only after the first request, so a fresh deployment has none yet.
          skipMetricValidation: true
        }
      ]
    }
    actions: [
      {
        actionGroupId: operators.id
      }
    ]
  }
}
