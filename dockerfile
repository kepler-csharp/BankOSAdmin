# ─────────────────────────────────────────
# API — ASP.NET Core 10 - PRODUCTION
# ─────────────────────────────────────────
version: '3.9'

services:
  admin-tickets:
    container_name: BankAdmin

    build:
      context: .
      dockerfile: Dockerfile

    image: ${DOCKER_USER}/bankadmin:latest

    ports:
      - "5001:8080"

    restart: unless-stopped
    
    environment:
      ASPNETCORE_ENVIRONMENT: Production
      Email__Host: ${SMTP_HOST}
      Email__Port: ${SMTP_PORT}
      Email__Username: ${SMTP_USERNAME}
      Email__Password: ${SMTP_PASSWORD}
      Email__From: ${SMTP_ADDRESS}
      Email__FromName: "BankOs Admin"
      Email__Enabled: true
      
      OpenAI__ApiKey: ${OPENAI_API_KEY}
      OpenAI__Model: gpt-4o-mini
      
      Database__Host: ${DB_HOST}
      Database__Port: ${DB_PORT}
      Database__User: ${DB_USER}
      Database__Password: ${DB_PASSWORD}
      Database__CentralDb: ${DB_CENTRAL}
      Database__TenantDbPrefix: ${DB_TENANT_PREFIX}
      Database__MDomainSuffix: ${DB_DOMAIN_SUFFIX}
      
      Branding__SupportEmail: "udeaismael@gmail.com"
      Branding__PortalUrl: "bank-os-admin.duckdns.org"