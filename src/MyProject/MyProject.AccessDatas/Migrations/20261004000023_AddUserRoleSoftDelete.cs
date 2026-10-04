using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyProject.AccessDatas.Migrations
{
    /// <inheritdoc />
    public partial class AddUserRoleSoftDelete : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedAt",
                table: "RoleView",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeletedBy",
                table: "RoleView",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsDeleted",
                table: "RoleView",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedAt",
                table: "MyUser",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeletedBy",
                table: "MyUser",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsDeleted",
                table: "MyUser",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeletedAt",
                table: "RoleView");

            migrationBuilder.DropColumn(
                name: "DeletedBy",
                table: "RoleView");

            migrationBuilder.DropColumn(
                name: "IsDeleted",
                table: "RoleView");

            migrationBuilder.DropColumn(
                name: "DeletedAt",
                table: "MyUser");

            migrationBuilder.DropColumn(
                name: "DeletedBy",
                table: "MyUser");

            migrationBuilder.DropColumn(
                name: "IsDeleted",
                table: "MyUser");
        }
    }
}
