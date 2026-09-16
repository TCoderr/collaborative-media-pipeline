# ─────────────────────────────────────────────────────────────────────────────
# Quire — observability.
#
# Log Analytics and Application Insights receive the telemetry, but the
# application emits plain OpenTelemetry over OTLP and never references an Azure
# SDK. Moving to another backend is a change to one endpoint variable.
#
# The alerts below are the ones that would otherwise be discovered by a user:
# work stuck in the outbox, media piling up in the dead-letter queue, and the
# API failing enough requests to matter.
# ─────────────────────────────────────────────────────────────────────────────

terraform {
  required_version = ">= 1.16.3, < 2.0.0"

  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 5.5"
    }
  }
}

resource "azurerm_log_analytics_workspace" "main" {
  name                = "log-${var.name_prefix}"
  resource_group_name = var.resource_group_name
  location            = var.location
  sku                 = "PerGB2018"
  retention_in_days   = var.log_retention_days
  tags                = var.tags
}

resource "azurerm_application_insights" "main" {
  name                = "appi-${var.name_prefix}"
  resource_group_name = var.resource_group_name
  location            = var.location
  workspace_id        = azurerm_log_analytics_workspace.main.id
  application_type    = "web"

  # Sampling is the collector's decision, not the application's: the app emits
  # everything and the pipeline decides what to keep.
  sampling_percentage = var.sampling_percentage

  tags = var.tags
}

resource "azurerm_monitor_action_group" "oncall" {
  count = length(var.alert_email_addresses) == 0 ? 0 : 1

  name                = "ag-oncall-${var.name_prefix}"
  resource_group_name = var.resource_group_name
  short_name          = "d4oncall"
  tags                = var.tags

  dynamic "email_receiver" {
    for_each = var.alert_email_addresses
    content {
      name          = "email-${email_receiver.key}"
      email_address = email_receiver.value
    }
  }
}

# ── Alerts on the pipeline's own signals ─────────────────────────────────────
# The outbox is the system's promise that accepted work will happen. A row that
# has sat unpublished for minutes means that promise is not being kept, and
# nothing user-facing will show it until a collaborator notices a stuck upload.
resource "azurerm_monitor_scheduled_query_rules_alert_v2" "outbox_backlog" {
  count = length(var.alert_email_addresses) == 0 ? 0 : 1

  name                = "alert-outbox-backlog-${var.name_prefix}"
  resource_group_name = var.resource_group_name
  location            = var.location
  severity            = 2
  scopes              = [azurerm_application_insights.main.id]
  description         = "Outbox rows have been pending longer than the publisher's backoff should allow."

  evaluation_frequency = "PT5M"
  window_duration      = "PT15M"

  criteria {
    query                   = <<-KQL
      customMetrics
      | where name == "collaborative-media-pipeline.outbox.oldest.age"
      | summarize MaxAge = max(value) by bin(timestamp, 5m)
      | where MaxAge > ${var.outbox_age_alert_seconds}
    KQL
    time_aggregation_method = "Maximum"
    metric_measure_column   = "MaxAge"
    threshold               = var.outbox_age_alert_seconds
    operator                = "GreaterThan"

    failing_periods {
      minimum_failing_periods_to_trigger_alert = 2
      number_of_evaluation_periods             = 2
    }
  }

  action {
    action_groups = [azurerm_monitor_action_group.oncall[0].id]
  }

  tags = var.tags
}

# Dead-lettered media is work the system has given up on. It is not lost - the
# source is intact and a replay is one command - but nobody finds out unless
# this fires.
resource "azurerm_monitor_scheduled_query_rules_alert_v2" "dead_letters" {
  count = length(var.alert_email_addresses) == 0 ? 0 : 1

  name                = "alert-dead-letters-${var.name_prefix}"
  resource_group_name = var.resource_group_name
  location            = var.location
  severity            = 2
  scopes              = [azurerm_application_insights.main.id]
  description         = "Media processing jobs have been dead-lettered. See RUNBOOK.md for the replay procedure."

  evaluation_frequency = "PT15M"
  window_duration      = "PT1H"

  criteria {
    query                   = <<-KQL
      customMetrics
      | where name == "collaborative-media-pipeline.processing.jobs.failed"
      | extend kind = tostring(customDimensions["failure_kind"])
      | where kind == "Permanent"
      | summarize Failures = sum(value) by bin(timestamp, 15m)
    KQL
    time_aggregation_method = "Total"
    metric_measure_column   = "Failures"
    threshold               = var.dead_letter_alert_threshold
    operator                = "GreaterThan"

    failing_periods {
      minimum_failing_periods_to_trigger_alert = 1
      number_of_evaluation_periods             = 1
    }
  }

  action {
    action_groups = [azurerm_monitor_action_group.oncall[0].id]
  }

  tags = var.tags
}

# The API's own SLO: 5xx above 1% of requests.
resource "azurerm_monitor_scheduled_query_rules_alert_v2" "api_error_rate" {
  count = length(var.alert_email_addresses) == 0 ? 0 : 1

  name                = "alert-api-error-rate-${var.name_prefix}"
  resource_group_name = var.resource_group_name
  location            = var.location
  severity            = 1
  scopes              = [azurerm_application_insights.main.id]
  description         = "API 5xx rate exceeded its SLO. See docs/ARCHITECTURE.md for the objectives."

  evaluation_frequency = "PT5M"
  window_duration      = "PT15M"

  criteria {
    query                   = <<-KQL
      requests
      | where cloud_RoleName == "collaborative-media-pipeline-api"
      | where url !contains "/health/"
      | summarize ErrorRate = 100.0 * countif(toint(resultCode) >= 500) / count() by bin(timestamp, 5m)
    KQL
    time_aggregation_method = "Average"
    metric_measure_column   = "ErrorRate"
    threshold               = var.api_error_rate_slo_percent
    operator                = "GreaterThan"

    failing_periods {
      minimum_failing_periods_to_trigger_alert = 2
      number_of_evaluation_periods             = 3
    }
  }

  action {
    action_groups = [azurerm_monitor_action_group.oncall[0].id]
  }

  tags = var.tags
}
