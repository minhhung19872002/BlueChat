using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace BlueChat.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "audit");

            migrationBuilder.EnsureSchema(
                name: "admin");

            migrationBuilder.EnsureSchema(
                name: "tenant");

            migrationBuilder.EnsureSchema(
                name: "org");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:pg_trgm", ",,")
                .Annotation("Npgsql:PostgresExtension:unaccent", ",,");

            migrationBuilder.CreateTable(
                name: "audit_logs",
                schema: "audit",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    actor = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    ip = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    user_agent = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    action = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    entity = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    entity_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    old_value = table.Column<string>(type: "jsonb", nullable: true),
                    new_value = table.Column<string>(type: "jsonb", nullable: true),
                    result = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    prev_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    hash = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_logs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "role_assignments",
                schema: "admin",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_role_assignments", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "tenants",
                schema: "tenant",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    blue_id_tenant_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    plan = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    default_locale = table.Column<string>(type: "character varying(35)", maxLength: 35, nullable: false),
                    default_time_zone = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    data_region = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    limits = table.Column<string>(type: "jsonb", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tenants", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "user_settings",
                schema: "org",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    settings = table.Column<string>(type: "jsonb", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_settings", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "users_cache",
                schema: "org",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    blue_id_user_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    display_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    avatar_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    is_guest = table.Column<bool>(type: "boolean", nullable: false),
                    department = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    job_title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    manager_id = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_from_blue_id_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_users_cache", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_audit_logs_tenant_id_occurred_at",
                schema: "audit",
                table: "audit_logs",
                columns: new[] { "tenant_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_role_assignments_tenant_id",
                schema: "admin",
                table: "role_assignments",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_role_assignments_tenant_id_user_id_role",
                schema: "admin",
                table: "role_assignments",
                columns: new[] { "tenant_id", "user_id", "role" },
                unique: true,
                filter: "deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_tenants_blue_id_tenant_id",
                schema: "tenant",
                table: "tenants",
                column: "blue_id_tenant_id",
                unique: true,
                filter: "deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_tenants_tenant_id",
                schema: "tenant",
                table: "tenants",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_user_settings_tenant_id",
                schema: "org",
                table: "user_settings",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_user_settings_tenant_id_user_id",
                schema: "org",
                table: "user_settings",
                columns: new[] { "tenant_id", "user_id" },
                unique: true,
                filter: "deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_users_cache_tenant_id",
                schema: "org",
                table: "users_cache",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_users_cache_tenant_id_blue_id_user_id",
                schema: "org",
                table: "users_cache",
                columns: new[] { "tenant_id", "blue_id_user_id" },
                unique: true,
                filter: "deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_users_cache_tenant_id_email",
                schema: "org",
                table: "users_cache",
                columns: new[] { "tenant_id", "email" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_logs",
                schema: "audit");

            migrationBuilder.DropTable(
                name: "role_assignments",
                schema: "admin");

            migrationBuilder.DropTable(
                name: "tenants",
                schema: "tenant");

            migrationBuilder.DropTable(
                name: "user_settings",
                schema: "org");

            migrationBuilder.DropTable(
                name: "users_cache",
                schema: "org");
        }
    }
}
